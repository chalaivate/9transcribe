namespace NineTranscribe.Api;

public enum TranscriptionErrorKind
{
    NoApiKey,
    InvalidApiKey,
    InsufficientQuota,
    RateLimited,
    AudioTooShort,
    PayloadTooLarge,
    BadRequest,
    Timeout,
    NetworkUnavailable,
    ServerError,
    Cancelled,
    Unknown,
}

/// <summary>
/// A transcription failure already translated into something the overlay can show.
/// <see cref="UserMessageThai"/> is always safe to display; raw API text goes to the log.
/// </summary>
public sealed class TranscriptionException : Exception
{
    public TranscriptionException(
        TranscriptionErrorKind kind,
        string userMessageThai,
        int? httpStatus = null,
        string? apiErrorCode = null,
        string? detail = null,
        Exception? innerException = null)
        : base(detail ?? userMessageThai, innerException)
    {
        Kind = kind;
        UserMessageThai = userMessageThai;
        HttpStatus = httpStatus;
        ApiErrorCode = apiErrorCode;
    }

    public TranscriptionErrorKind Kind { get; }

    public string UserMessageThai { get; }

    public int? HttpStatus { get; }

    public string? ApiErrorCode { get; }

    /// <summary>True when trying the exact same request again could plausibly succeed.</summary>
    public bool IsRetryable => Kind is TranscriptionErrorKind.RateLimited
        or TranscriptionErrorKind.ServerError
        or TranscriptionErrorKind.Timeout
        or TranscriptionErrorKind.NetworkUnavailable;
}
