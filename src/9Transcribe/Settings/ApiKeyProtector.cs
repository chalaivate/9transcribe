using System.Security.Cryptography;
using System.Text;

namespace NineTranscribe.Settings;

/// <summary>
/// Encrypts the OpenAI API key with DPAPI under the current user account, so the key in
/// settings.json is unreadable by other users and unusable if the file is copied off the
/// machine. The plaintext key exists only in memory and is never written to the log.
/// </summary>
public static class ApiKeyProtector
{
    // Additional entropy binds the blob to this application, not just to the user.
    private static readonly byte[] Entropy =
    {
        0x39, 0x54, 0x52, 0x53, 0x1A, 0x6B, 0xC4, 0x0E,
        0x7D, 0x92, 0x35, 0xF1, 0x48, 0xAE, 0x63, 0x2C,
    };

    /// <returns>Base64 of the protected blob, or null when <paramref name="apiKey"/> is blank.</returns>
    public static string? Protect(string? apiKey)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return null;
        }

        byte[] plain = Encoding.UTF8.GetBytes(apiKey.Trim());
        try
        {
            byte[] blob = ProtectedData.Protect(plain, Entropy, DataProtectionScope.CurrentUser);
            return Convert.ToBase64String(blob);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plain);
        }
    }

    /// <returns>
    /// The plaintext key, or null when nothing is stored or the blob cannot be decrypted —
    /// which is the expected outcome after the portable exe moves to another user or machine.
    /// </returns>
    public static string? Unprotect(string? protectedBase64)
    {
        if (string.IsNullOrWhiteSpace(protectedBase64))
        {
            return null;
        }

        try
        {
            byte[] blob = Convert.FromBase64String(protectedBase64);
            byte[] plain = ProtectedData.Unprotect(blob, Entropy, DataProtectionScope.CurrentUser);
            try
            {
                return Encoding.UTF8.GetString(plain);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(plain);
            }
        }
        catch (CryptographicException)
        {
            return null;
        }
        catch (FormatException)
        {
            return null;
        }
    }
}
