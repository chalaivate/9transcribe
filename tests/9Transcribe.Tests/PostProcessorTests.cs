using NineTranscribe.Processing;
using NineTranscribe.Settings;
using Xunit;

namespace NineTranscribe.Tests;

public sealed class PostProcessorTests
{
    [Fact]
    public void Process_TrimsWhitespaceTheModelAppends()
    {
        var settings = new AppSettings();

        Assert.Equal("สวัสดีครับ", TranscriptPostProcessor.Process("  สวัสดีครับ\n", settings));
    }

    [Fact]
    public void Process_AppliesReplacementsInListOrder()
    {
        var settings = new AppSettings();
        settings.Replacements.Add(new ReplacementRuleSetting { Find = "พาวเวอร์บีไอ", Replace = "Power BI" });
        settings.Replacements.Add(new ReplacementRuleSetting { Find = "Power BI", Replace = "Power BI Desktop" });

        string result = TranscriptPostProcessor.Process("เปิดพาวเวอร์บีไอหน่อย", settings);

        Assert.Equal("เปิดPower BI Desktopหน่อย", result);
    }

    [Fact]
    public void Process_ReplacementIsCaseInsensitiveUnlessMatchCaseIsSet()
    {
        var insensitive = new AppSettings();
        insensitive.Replacements.Add(new ReplacementRuleSetting { Find = "dax", Replace = "DAX" });

        var sensitive = new AppSettings();
        sensitive.Replacements.Add(new ReplacementRuleSetting
        {
            Find = "dax",
            Replace = "DAX",
            MatchCase = true,
        });

        Assert.Equal("ใช้ DAX ได้", TranscriptPostProcessor.Process("ใช้ Dax ได้", insensitive));
        Assert.Equal("ใช้ Dax ได้", TranscriptPostProcessor.Process("ใช้ Dax ได้", sensitive));
    }

    [Fact]
    public void Process_CollapsesRepeatedSpacesWhenEnabled()
    {
        var settings = new AppSettings { CollapseSpaces = true };

        Assert.Equal("hello world", TranscriptPostProcessor.Process("hello    world", settings));
    }

    [Theory]
    [InlineData(TrailingText.None, "สวัสดี", "สวัสดี")]
    [InlineData(TrailingText.Space, "สวัสดี", "สวัสดี ")]
    [InlineData(TrailingText.Smart, "สวัสดี", "สวัสดี")]
    [InlineData(TrailingText.Smart, "open Excel", "open Excel ")]
    [InlineData(TrailingText.Smart, "ราคา 100", "ราคา 100 ")]
    public void Process_AppendsTrailingTextPerMode(TrailingText mode, string input, string expected)
    {
        var settings = new AppSettings { TrailingText = mode };

        Assert.Equal(expected, TranscriptPostProcessor.Process(input, settings));
    }

    [Fact]
    public void Process_WithBlankInput_ReturnsEmpty()
    {
        Assert.Equal(string.Empty, TranscriptPostProcessor.Process("   \n ", new AppSettings()));
        Assert.Equal(string.Empty, TranscriptPostProcessor.Process(null, new AppSettings()));
    }

    [Fact]
    public void Process_WhenAReplacementEmptiesTheText_ReturnsEmpty()
    {
        var settings = new AppSettings();
        settings.Replacements.Add(new ReplacementRuleSetting { Find = "เอ่อ", Replace = "" });

        Assert.Equal(string.Empty, TranscriptPostProcessor.Process("เอ่อ", settings));
    }

    [Fact]
    public void ForPreview_TruncatesLongTextWithAnEllipsis()
    {
        string text = new('ก', 400);

        string preview = TranscriptPostProcessor.ForPreview(text, maxChars: 100);

        Assert.Equal(101, preview.Length);
        Assert.EndsWith("…", preview, StringComparison.Ordinal);
    }
}
