using System.Net;
using System.Net.Http;
using NineTranscribe.Api;
using Xunit;

namespace NineTranscribe.Tests;

public sealed class OpenAiClientTests
{
    private static readonly byte[] Wav = new byte[64];

    [Fact]
    public void ParseTranscript_ReadsTheTextField()
    {
        Assert.Equal(
            "สวัสดีครับ",
            OpenAiTranscriptionClient.ParseTranscript("""{"text":"สวัสดีครับ","usage":{}}"""));
    }

    [Fact]
    public void ParseTranscript_FallsBackToPlainBody()
    {
        Assert.Equal("สวัสดี", OpenAiTranscriptionClient.ParseTranscript("สวัสดี"));
        Assert.Equal(string.Empty, OpenAiTranscriptionClient.ParseTranscript(""));
    }

    [Fact]
    public async Task TranscribeAsync_SendsEveryRequiredMultipartField()
    {
        HttpRequestMessage? captured = null;
        string? body = null;
        var handler = new StubHandler(async request =>
        {
            captured = request;
            body = await request.Content!.ReadAsStringAsync();
            return Json(HttpStatusCode.OK, """{"text":"ok"}""");
        });

        var client = new OpenAiTranscriptionClient(() => "sk-test", new HttpClient(handler));
        TranscriptionResult result = await client.TranscribeAsync(
            Wav,
            new TranscriptionRequestOptions("gpt-4o-transcribe", "th", "คำศัพท์: DAX"),
            CancellationToken.None);

        Assert.Equal("ok", result.Text);
        Assert.Equal(HttpMethod.Post, captured!.Method);
        Assert.Equal("Bearer", captured.Headers.Authorization!.Scheme);
        Assert.Equal("sk-test", captured.Headers.Authorization.Parameter);
        Assert.Contains("filename=\"audio.wav\"", body, StringComparison.Ordinal);
        Assert.Contains("name=\"model\"", body, StringComparison.Ordinal);
        Assert.Contains("gpt-4o-transcribe", body, StringComparison.Ordinal);
        Assert.Contains("name=\"language\"", body, StringComparison.Ordinal);
        Assert.Contains("name=\"prompt\"", body, StringComparison.Ordinal);
        // gpt-4o models reject verbose_json outright.
        Assert.Contains("name=\"response_format\"", body, StringComparison.Ordinal);
        Assert.Contains("json", body, StringComparison.Ordinal);
        Assert.DoesNotContain("verbose_json", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TranscribeAsync_WithNoApiKey_FailsBeforeAnyRequest()
    {
        var handler = new StubHandler(_ => throw new InvalidOperationException("must not be called"));
        var client = new OpenAiTranscriptionClient(() => null, new HttpClient(handler));

        TranscriptionException error = await Assert.ThrowsAsync<TranscriptionException>(
            () => client.TranscribeAsync(
                Wav,
                new TranscriptionRequestOptions("gpt-4o-transcribe", "th", null),
                CancellationToken.None));

        Assert.Equal(TranscriptionErrorKind.NoApiKey, error.Kind);
    }

    [Fact]
    public async Task TranscribeAsync_On401_DoesNotRetry()
    {
        int calls = 0;
        var handler = new StubHandler(_ =>
        {
            calls++;
            return Task.FromResult(Json(
                HttpStatusCode.Unauthorized,
                """{"error":{"message":"Incorrect API key provided","code":"invalid_api_key"}}"""));
        });

        var client = new OpenAiTranscriptionClient(() => "sk-bad", new HttpClient(handler));
        TranscriptionException error = await Assert.ThrowsAsync<TranscriptionException>(
            () => client.TranscribeAsync(
                Wav,
                new TranscriptionRequestOptions("gpt-4o-transcribe", "th", null),
                CancellationToken.None));

        Assert.Equal(1, calls);
        Assert.Equal(TranscriptionErrorKind.InvalidApiKey, error.Kind);
        Assert.Contains("API Key", error.UserMessageThai, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TranscribeAsync_On500_RetriesThenSucceeds()
    {
        int calls = 0;
        var handler = new StubHandler(_ =>
        {
            calls++;
            return Task.FromResult(calls < 3
                ? Json(HttpStatusCode.InternalServerError, """{"error":{"message":"boom"}}""")
                : Json(HttpStatusCode.OK, """{"text":"หลังลองใหม่"}"""));
        });

        var client = new OpenAiTranscriptionClient(() => "sk-test", new HttpClient(handler));
        TranscriptionResult result = await client.TranscribeAsync(
            Wav,
            new TranscriptionRequestOptions("gpt-4o-transcribe", "th", null),
            CancellationToken.None);

        Assert.Equal(3, calls);
        Assert.Equal("หลังลองใหม่", result.Text);
    }

    [Fact]
    public async Task TranscribeAsync_OnQuotaExhausted_ReportsQuotaNotRateLimit()
    {
        var handler = new StubHandler(_ => Task.FromResult(Json(
            HttpStatusCode.TooManyRequests,
            """{"error":{"message":"You exceeded your quota","code":"insufficient_quota"}}""")));

        var client = new OpenAiTranscriptionClient(() => "sk-test", new HttpClient(handler));
        TranscriptionException error = await Assert.ThrowsAsync<TranscriptionException>(
            () => client.TranscribeAsync(
                Wav,
                new TranscriptionRequestOptions("gpt-4o-transcribe", "th", null),
                CancellationToken.None));

        Assert.Equal(TranscriptionErrorKind.InsufficientQuota, error.Kind);
    }

    [Fact]
    public async Task TestApiKeyAsync_ReportsAMissingModel()
    {
        var handler = new StubHandler(_ => Task.FromResult(Json(
            HttpStatusCode.OK,
            """{"data":[{"id":"whisper-1"},{"id":"gpt-4o"}]}""")));

        var client = new OpenAiTranscriptionClient(() => "sk-test", new HttpClient(handler));
        ApiKeyTestResult result = await client.TestApiKeyAsync(
            "sk-test",
            "gpt-4o-transcribe",
            CancellationToken.None);

        Assert.True(result.Success);
        Assert.False(result.ModelAccessible);
    }

    [Fact]
    public async Task TestApiKeyAsync_WhenModelsIsForbidden_FallsBackToATranscriptionProbe()
    {
        // A project-scoped key can be denied /v1/models while transcription still works.
        var handler = new StubHandler(request => Task.FromResult(
            request.RequestUri!.AbsolutePath.Contains("models", StringComparison.Ordinal)
                ? Json(HttpStatusCode.Forbidden, """{"error":{"message":"insufficient permissions"}}""")
                : Json(HttpStatusCode.OK, """{"text":""}""")));

        var client = new OpenAiTranscriptionClient(() => "sk-test", new HttpClient(handler));
        ApiKeyTestResult result = await client.TestApiKeyAsync(
            "sk-test",
            "gpt-4o-transcribe",
            CancellationToken.None);

        Assert.True(result.Success);
    }

    [Fact]
    public async Task TestApiKeyAsync_WhenTheProbeIsAlsoRejected_ReportsFailure()
    {
        var handler = new StubHandler(_ => Task.FromResult(Json(
            HttpStatusCode.Forbidden,
            """{"error":{"message":"insufficient permissions"}}""")));

        var client = new OpenAiTranscriptionClient(() => "sk-test", new HttpClient(handler));
        ApiKeyTestResult result = await client.TestApiKeyAsync(
            "sk-test",
            "gpt-4o-transcribe",
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(TranscriptionErrorKind.InvalidApiKey, result.Error);
    }

    [Fact]
    public void ErrorParser_ExtractsMessageAndCode()
    {
        (string? message, string? code) = OpenAiErrorParser.ParseErrorBody(
            """{"error":{"message":"บึ้ม","code":"rate_limit_exceeded"}}""");

        Assert.Equal("บึ้ม", message);
        Assert.Equal("rate_limit_exceeded", code);
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string body) => new(status)
    {
        Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
    };

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, Task<HttpResponseMessage>> _responder;

        public StubHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> responder) =>
            _responder = responder;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => _responder(request);
    }
}
