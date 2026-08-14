using System.Net;
using System.Text.Json;

namespace NineTranscribe.Api;

/// <summary>
/// Maps an OpenAI error response onto a <see cref="TranscriptionErrorKind"/> and the Thai
/// sentence the user sees. The overlay has room for one line, so each message says what
/// happened and what to do about it, without any English error codes.
/// </summary>
public static class OpenAiErrorParser
{
    public static TranscriptionException FromResponse(HttpStatusCode status, string? body)
    {
        (string? message, string? code) = ParseErrorBody(body);
        int http = (int)status;

        switch (status)
        {
            case HttpStatusCode.Unauthorized:
                return new TranscriptionException(
                    TranscriptionErrorKind.InvalidApiKey,
                    "API Key ไม่ถูกต้องหรือถูกยกเลิก กรุณาตรวจสอบในหน้าตั้งค่า",
                    http, code, message);

            case HttpStatusCode.Forbidden:
                return new TranscriptionException(
                    TranscriptionErrorKind.InvalidApiKey,
                    "API Key นี้ไม่มีสิทธิ์ใช้งานการถอดเสียง กรุณาตรวจสอบสิทธิ์ของคีย์",
                    http, code, message);

            case HttpStatusCode.TooManyRequests:
                return string.Equals(code, "insufficient_quota", StringComparison.OrdinalIgnoreCase)
                    ? new TranscriptionException(
                        TranscriptionErrorKind.InsufficientQuota,
                        "เครดิต OpenAI หมด กรุณาเติมเครดิตในบัญชี OpenAI",
                        http, code, message)
                    : new TranscriptionException(
                        TranscriptionErrorKind.RateLimited,
                        "เรียกใช้งานถี่เกินไป กรุณารอสักครู่แล้วลองใหม่",
                        http, code, message);

            case HttpStatusCode.RequestEntityTooLarge:
                return new TranscriptionException(
                    TranscriptionErrorKind.PayloadTooLarge,
                    "เสียงที่บันทึกยาวเกินไป กรุณาพูดเป็นช่วงสั้นลง",
                    http, code, message);

            case HttpStatusCode.BadRequest when LooksTooShort(message):
                return new TranscriptionException(
                    TranscriptionErrorKind.AudioTooShort,
                    "ไม่พบเสียงพูด",
                    http, code, message);

            case HttpStatusCode.BadRequest:
                return new TranscriptionException(
                    TranscriptionErrorKind.BadRequest,
                    string.IsNullOrWhiteSpace(message)
                        ? "คำขอไม่ถูกต้อง กรุณาตรวจสอบการตั้งค่าโมเดล"
                        : $"คำขอไม่ถูกต้อง: {message}",
                    http, code, message);
        }

        if (http >= 500)
        {
            return new TranscriptionException(
                TranscriptionErrorKind.ServerError,
                "เซิร์ฟเวอร์ OpenAI ขัดข้องชั่วคราว กรุณาลองใหม่อีกครั้ง",
                http, code, message);
        }

        return new TranscriptionException(
            TranscriptionErrorKind.Unknown,
            string.IsNullOrWhiteSpace(message)
                ? $"เกิดข้อผิดพลาดที่ไม่รู้จัก (HTTP {http})"
                : $"เกิดข้อผิดพลาด: {message}",
            http, code, message);
    }

    public static TranscriptionException NoApiKey() => new(
        TranscriptionErrorKind.NoApiKey,
        "ยังไม่ได้ตั้งค่า API Key — เปิดหน้าตั้งค่าเพื่อใส่ OpenAI API Key");

    public static TranscriptionException Timeout(Exception? inner = null) => new(
        TranscriptionErrorKind.Timeout,
        "หมดเวลาเชื่อมต่อกับ OpenAI กรุณาลองใหม่",
        innerException: inner);

    public static TranscriptionException Network(Exception inner) => new(
        TranscriptionErrorKind.NetworkUnavailable,
        "เชื่อมต่ออินเทอร์เน็ตไม่ได้ กรุณาตรวจสอบเครือข่าย",
        detail: inner.Message,
        innerException: inner);

    /// <summary>Extracts <c>error.message</c> and <c>error.code</c> from an API error body.</summary>
    public static (string? Message, string? Code) ParseErrorBody(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return (null, null);
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(body);
            if (!document.RootElement.TryGetProperty("error", out JsonElement error)
                || error.ValueKind != JsonValueKind.Object)
            {
                return (null, null);
            }

            string? message = error.TryGetProperty("message", out JsonElement m)
                && m.ValueKind == JsonValueKind.String
                    ? m.GetString()
                    : null;
            string? code = error.TryGetProperty("code", out JsonElement c)
                && c.ValueKind == JsonValueKind.String
                    ? c.GetString()
                    : null;
            return (message, code);
        }
        catch (JsonException)
        {
            return (null, null);
        }
    }

    private static bool LooksTooShort(string? message) =>
        message is not null
        && (message.Contains("too short", StringComparison.OrdinalIgnoreCase)
            || message.Contains("shorter than the minimum", StringComparison.OrdinalIgnoreCase));
}
