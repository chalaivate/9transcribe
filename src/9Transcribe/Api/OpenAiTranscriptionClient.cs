using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using NineTranscribe.Audio;

namespace NineTranscribe.Api;

public sealed record TranscriptionRequestOptions(
    string Model,
    string? Language,
    string? Prompt,
    double Temperature = 0.0,
    int TimeoutSeconds = 30);

public sealed record TranscriptionResult(string Text, TimeSpan Elapsed, string Model);

public sealed record ApiKeyTestResult(
    bool Success,
    string MessageThai,
    bool ModelAccessible = true,
    TranscriptionErrorKind? Error = null);

/// <summary>
/// Talks to <c>POST /v1/audio/transcriptions</c> directly over HTTP rather than through
/// the OpenAI SDK: the app needs exact control of the multipart fields (gpt-4o models
/// reject <c>verbose_json</c>), the raw error body for its Thai error messages, and the
/// <c>Retry-After</c> header.
/// </summary>
public sealed class OpenAiTranscriptionClient
{
    private const string TranscriptionsUrl = "https://api.openai.com/v1/audio/transcriptions";
    private const string ModelsUrl = "https://api.openai.com/v1/models";
    private const int MaxAttempts = 3;
    private const int MaxTotalRetryDelayMs = 15_000;

    private static readonly HttpClient SharedClient = CreateDefaultClient();

    private readonly Func<string?> _apiKeyProvider;
    private readonly HttpClient _http;

    public OpenAiTranscriptionClient(Func<string?> apiKeyProvider, HttpClient? httpClient = null)
    {
        _apiKeyProvider = apiKeyProvider ?? throw new ArgumentNullException(nameof(apiKeyProvider));
        _http = httpClient ?? SharedClient;
    }

    /// <summary>Raised before each retry so the overlay can say "กำลังลองใหม่…".</summary>
    public event EventHandler<int>? Retrying;

    public async Task<TranscriptionResult> TranscribeAsync(
        byte[] wavBytes,
        TranscriptionRequestOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(wavBytes);
        ArgumentNullException.ThrowIfNull(options);

        string? apiKey = _apiKeyProvider();
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw OpenAiErrorParser.NoApiKey();
        }

        long start = Environment.TickCount64;
        int retryBudgetMs = MaxTotalRetryDelayMs;
        TranscriptionException? last = null;

        for (int attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                string text = await SendOnceAsync(wavBytes, options, apiKey, cancellationToken)
                    .ConfigureAwait(false);
                return new TranscriptionResult(
                    text,
                    TimeSpan.FromMilliseconds(Environment.TickCount64 - start),
                    options.Model);
            }
            catch (TranscriptionException ex) when (ex.IsRetryable && attempt < MaxAttempts)
            {
                last = ex;
                int delayMs = NextDelayMs(attempt, ex);
                if (delayMs > retryBudgetMs)
                {
                    break;
                }

                retryBudgetMs -= delayMs;
                Retrying?.Invoke(this, attempt);
                await Task.Delay(delayMs, cancellationToken).ConfigureAwait(false);
            }
        }

        throw last ?? new TranscriptionException(
            TranscriptionErrorKind.Unknown,
            "ถอดเสียงไม่สำเร็จ กรุณาลองใหม่อีกครั้ง");
    }

    /// <summary>
    /// Validates a key without spending tokens by listing models. Project-scoped keys can
    /// be denied that endpoint, in which case the caller still gets a usable verdict from
    /// the HTTP status alone.
    /// </summary>
    public async Task<ApiKeyTestResult> TestApiKeyAsync(
        string? apiKey,
        string modelToCheck,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return new ApiKeyTestResult(
                false,
                "ยังไม่ได้ใส่ API Key",
                Error: TranscriptionErrorKind.NoApiKey);
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, ModelsUrl);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey.Trim());

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));

        try
        {
            using HttpResponseMessage response = await _http
                .SendAsync(request, HttpCompletionOption.ResponseContentRead, timeout.Token)
                .ConfigureAwait(false);
            string body = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);

            if (response.StatusCode == HttpStatusCode.Forbidden)
            {
                // A project-scoped key can be denied the models endpoint while transcription
                // works fine, so the only honest answer is to try a real transcription.
                return await ProbeWithSilenceAsync(apiKey, cancellationToken).ConfigureAwait(false);
            }

            if (!response.IsSuccessStatusCode)
            {
                TranscriptionException error = OpenAiErrorParser.FromResponse(response.StatusCode, body);
                return new ApiKeyTestResult(false, error.UserMessageThai, Error: error.Kind);
            }

            bool found = ContainsModel(body, modelToCheck);
            return found
                ? new ApiKeyTestResult(true, "เชื่อมต่อสำเร็จ")
                : new ApiKeyTestResult(
                    true,
                    $"เชื่อมต่อสำเร็จ แต่ไม่พบโมเดล {modelToCheck} ในบัญชีนี้",
                    ModelAccessible: false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new ApiKeyTestResult(
                false,
                "หมดเวลาเชื่อมต่อกับ OpenAI กรุณาลองใหม่",
                Error: TranscriptionErrorKind.Timeout);
        }
        catch (HttpRequestException ex)
        {
            return new ApiKeyTestResult(
                false,
                OpenAiErrorParser.Network(ex).UserMessageThai,
                Error: TranscriptionErrorKind.NetworkUnavailable);
        }
    }

    /// <summary>
    /// Transcribes a fraction of a second of silence with the cheapest model, purely to learn
    /// whether the key is accepted. Costs a rounding error and takes about a second.
    /// </summary>
    private async Task<ApiKeyTestResult> ProbeWithSilenceAsync(string apiKey, CancellationToken cancellationToken)
    {
        var options = new TranscriptionRequestOptions(
            "gpt-4o-mini-transcribe",
            Language: null,
            Prompt: null,
            TimeoutSeconds: 20);

        try
        {
            await SendOnceAsync(
                    WavUtil.CreateSilence(TimeSpan.FromMilliseconds(300)),
                    options,
                    apiKey,
                    cancellationToken)
                .ConfigureAwait(false);
            return new ApiKeyTestResult(true, "เชื่อมต่อสำเร็จ");
        }
        catch (TranscriptionException ex) when (ex.Kind == TranscriptionErrorKind.AudioTooShort)
        {
            // The request was authorized; it only failed on the audio itself.
            return new ApiKeyTestResult(true, "เชื่อมต่อสำเร็จ");
        }
        catch (TranscriptionException ex)
        {
            return new ApiKeyTestResult(false, ex.UserMessageThai, Error: ex.Kind);
        }
    }

    private async Task<string> SendOnceAsync(
        byte[] wavBytes,
        TranscriptionRequestOptions options,
        string apiKey,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(options.TimeoutSeconds, 5, 300)));

        using var content = new MultipartFormDataContent();
        var audio = new ByteArrayContent(wavBytes);
        audio.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
        // The filename is what the API uses to sniff the container; without it the request
        // fails with "could not determine file format". Setting Content-Disposition by hand
        // (with the quotes included) keeps the wire format identical to every other client:
        // MultipartFormDataContent.Add would emit an unquoted name plus a filename* copy.
        audio.Headers.ContentDisposition = new ContentDispositionHeaderValue("form-data")
        {
            Name = "\"file\"",
            FileName = "\"audio.wav\"",
        };
        content.Add(audio);

        AddField(content, "model", options.Model);
        AddField(content, "response_format", "json");
        AddField(content, "temperature", options.Temperature.ToString("0.###", CultureInfo.InvariantCulture));

        ModelCapabilities capabilities = ModelCapabilities.For(options.Model);
        if (capabilities.SupportsLanguage && !string.IsNullOrWhiteSpace(options.Language))
        {
            AddField(content, "language", options.Language.Trim());
        }

        if (capabilities.SupportsPrompt && !string.IsNullOrWhiteSpace(options.Prompt))
        {
            AddField(content, "prompt", options.Prompt);
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, TranscriptionsUrl)
        {
            Content = content,
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey.Trim());

        HttpResponseMessage response;
        try
        {
            response = await _http
                .SendAsync(request, HttpCompletionOption.ResponseContentRead, timeout.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw OpenAiErrorParser.Timeout(ex);
        }
        catch (HttpRequestException ex)
        {
            throw OpenAiErrorParser.Network(ex);
        }

        using (response)
        {
            string body = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                TranscriptionException error = OpenAiErrorParser.FromResponse(response.StatusCode, body);
                if (error.Kind == TranscriptionErrorKind.RateLimited)
                {
                    RetryAfterSeconds = ReadRetryAfterSeconds(response);
                }

                throw error;
            }

            return ParseTranscript(body);
        }
    }

    private static void AddField(MultipartFormDataContent content, string name, string value)
    {
        // The charset stays on the part: the prompt field carries Thai, and a parser that
        // assumed latin-1 would mangle the custom vocabulary.
        var part = new StringContent(value, Encoding.UTF8);
        part.Headers.ContentDisposition = new ContentDispositionHeaderValue("form-data")
        {
            Name = $"\"{name}\"",
        };
        content.Add(part);
    }

    /// <summary>Seconds requested by the last 429, used to pace the next retry.</summary>
    private double? RetryAfterSeconds { get; set; }

    private int NextDelayMs(int attempt, TranscriptionException error)
    {
        double baseMs = attempt == 1 ? 1000 : 2500;

        if (error.Kind == TranscriptionErrorKind.RateLimited && RetryAfterSeconds is { } seconds)
        {
            baseMs = Math.Max(baseMs, seconds * 1000);
            RetryAfterSeconds = null;
        }

        double jitter = 0.8 + (Random.Shared.NextDouble() * 0.4);
        return (int)Math.Clamp(baseMs * jitter, 250, MaxTotalRetryDelayMs);
    }

    private static double? ReadRetryAfterSeconds(HttpResponseMessage response)
    {
        RetryConditionHeaderValue? header = response.Headers.RetryAfter;
        if (header is null)
        {
            return null;
        }

        if (header.Delta is { } delta)
        {
            return delta.TotalSeconds;
        }

        if (header.Date is { } date)
        {
            double seconds = (date - DateTimeOffset.UtcNow).TotalSeconds;
            return seconds > 0 ? seconds : null;
        }

        return null;
    }

    /// <summary>Reads <c>{"text": "..."}</c>, the shape every supported model returns for json.</summary>
    public static string ParseTranscript(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return string.Empty;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(body);
            if (document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("text", out JsonElement text)
                && text.ValueKind == JsonValueKind.String)
            {
                return text.GetString() ?? string.Empty;
            }
        }
        catch (JsonException)
        {
            // response_format=text (or a future shape) hands back a bare string.
            return body.Trim();
        }

        return string.Empty;
    }

    private static bool ContainsModel(string body, string modelId)
    {
        if (string.IsNullOrWhiteSpace(modelId))
        {
            return true;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(body);
            if (!document.RootElement.TryGetProperty("data", out JsonElement data)
                || data.ValueKind != JsonValueKind.Array)
            {
                return true;
            }

            foreach (JsonElement item in data.EnumerateArray())
            {
                if (item.TryGetProperty("id", out JsonElement id)
                    && id.ValueKind == JsonValueKind.String
                    && string.Equals(id.GetString(), modelId, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }
        catch (JsonException)
        {
            return true;
        }
    }

    private static HttpClient CreateDefaultClient()
    {
        var handler = new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
            AutomaticDecompression = DecompressionMethods.All,
        };

        return new HttpClient(handler)
        {
            // Per-request deadlines come from a linked CancellationTokenSource so that a
            // timeout can be told apart from a user cancellation.
            Timeout = Timeout.InfiniteTimeSpan,
        };
    }
}
