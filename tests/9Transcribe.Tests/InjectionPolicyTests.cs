using NineTranscribe.Injection;
using NineTranscribe.Settings;
using Xunit;

namespace NineTranscribe.Tests;

public sealed class InjectionPolicyTests
{
    private static readonly string[] ForceTyping = { "putty", "kitty" };
    private static readonly string[] ForceClipboard = { "mstsc", "vmware-vmx" };

    [Fact]
    public void Choose_WithNoOverride_KeepsTheConfiguredMethod()
    {
        Assert.Equal(
            InsertionMethod.ClipboardPaste,
            InsertionPolicy.Choose(InsertionMethod.ClipboardPaste, "notepad", ForceTyping, ForceClipboard));
        Assert.Equal(
            InsertionMethod.UnicodeTyping,
            InsertionPolicy.Choose(InsertionMethod.UnicodeTyping, "notepad", ForceTyping, ForceClipboard));
    }

    [Fact]
    public void Choose_ForATerminalThatEatsCtrlV_SwitchesToTyping()
    {
        Assert.Equal(
            InsertionMethod.UnicodeTyping,
            InsertionPolicy.Choose(InsertionMethod.ClipboardPaste, "putty", ForceTyping, ForceClipboard));
    }

    [Fact]
    public void Choose_ForARemoteDesktopThatDropsUnicode_SwitchesToTheClipboard()
    {
        Assert.Equal(
            InsertionMethod.ClipboardPaste,
            InsertionPolicy.Choose(InsertionMethod.UnicodeTyping, "mstsc", ForceTyping, ForceClipboard));
    }

    [Theory]
    [InlineData("PuTTY")]
    [InlineData("putty.exe")]
    [InlineData("  PuTTY.EXE  ")]
    public void Choose_MatchesRegardlessOfCaseExtensionOrPadding(string processName)
    {
        Assert.Equal(
            InsertionMethod.UnicodeTyping,
            InsertionPolicy.Choose(InsertionMethod.ClipboardPaste, processName, ForceTyping, ForceClipboard));
    }

    [Fact]
    public void Choose_WithAnEmptyProcessName_KeepsTheConfiguredMethod()
    {
        Assert.Equal(
            InsertionMethod.ClipboardPaste,
            InsertionPolicy.Choose(InsertionMethod.ClipboardPaste, string.Empty, ForceTyping, ForceClipboard));
    }

    [Fact]
    public void Choose_DoesNotMatchAProcessThatMerelyContainsThePattern()
    {
        Assert.Equal(
            InsertionMethod.ClipboardPaste,
            InsertionPolicy.Choose(
                InsertionMethod.ClipboardPaste,
                "puttygen",
                ForceTyping,
                ForceClipboard));
    }
}
