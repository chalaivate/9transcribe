namespace NineTranscribe.Interop;

/// <summary>
/// Marker written into <c>dwExtraInfo</c> of every synthetic input event the app sends.
/// The keyboard hook checks for it so our own Ctrl+V and Unicode typing can never be
/// mistaken for the user pressing a hotkey.
/// </summary>
internal static class InjectionTag
{
    /// <summary>"9TRS" as an ASCII-derived constant; any non-zero unique value would do.</summary>
    public static readonly UIntPtr Value = new(0x39545253u);

    public static bool IsOurs(UIntPtr extraInfo) => extraInfo == Value;
}
