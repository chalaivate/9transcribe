namespace NineTranscribe.Settings;

/// <summary>How transcribed text reaches the caret of the foreground application.</summary>
public enum InsertionMethod
{
    ClipboardPaste,
    UnicodeTyping,
}

/// <summary>What, if anything, is appended after every transcript.</summary>
public enum TrailingText
{
    None,

    /// <summary>
    /// Append a space only when the transcript ends in a Latin letter or digit.
    /// Thai runs words together, so an unconditional space breaks phrases mid-sentence.
    /// </summary>
    Smart,
    Space,
    NewLine,
}

/// <summary>One of eight edge/corner anchors on the working area of a monitor.</summary>
public enum OverlayPosition
{
    Top,
    Bottom,
    Left,
    Right,
    TopLeft,
    TopRight,
    BottomLeft,
    BottomRight,
}

public enum AppTheme
{
    System,
    Light,
    Dark,
}
