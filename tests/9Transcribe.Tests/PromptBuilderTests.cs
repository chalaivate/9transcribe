using NineTranscribe.Api;
using Xunit;

namespace NineTranscribe.Tests;

public sealed class PromptBuilderTests
{
    private static readonly ModelCapabilities Gpt4o = ModelCapabilities.For("gpt-4o-transcribe");
    private static readonly ModelCapabilities Whisper = ModelCapabilities.For("whisper-1");

    [Fact]
    public void Build_JoinsVocabularyAfterThePrefix()
    {
        string prompt = PromptBuilder.Build(
            "บทสนทนาภาษาไทยปนคำศัพท์อังกฤษ:",
            new[] { "Power BI", "DAX" },
            Gpt4o);

        Assert.Equal("บทสนทนาภาษาไทยปนคำศัพท์อังกฤษ: Power BI, DAX", prompt);
    }

    [Fact]
    public void Build_WithNoVocabulary_ReturnsJustThePrefix()
    {
        Assert.Equal("นำหน้า", PromptBuilder.Build("  นำหน้า  ", Array.Empty<string>(), Gpt4o));
    }

    [Fact]
    public void Build_DropsBlanksAndDuplicates()
    {
        string prompt = PromptBuilder.Build(null, new[] { "DAX", " ", "dax", "Excel", "" }, Gpt4o);

        Assert.Equal("DAX, Excel", prompt);
    }

    [Fact]
    public void Build_StaysWithinTheModelCharacterBudget()
    {
        IEnumerable<string> many = Enumerable.Range(0, 500).Select(i => $"term{i:0000}");

        string forWhisper = PromptBuilder.Build("นำ:", many, Whisper);
        string for4o = PromptBuilder.Build("นำ:", many, Gpt4o);

        Assert.True(forWhisper.Length <= Whisper.PromptCharBudget);
        Assert.True(for4o.Length <= Gpt4o.PromptCharBudget);
        // whisper-1 only reads its final 224 tokens, so its budget is much tighter.
        Assert.True(for4o.Length > forWhisper.Length);
    }

    [Fact]
    public void Build_TruncatesWholeTermsRatherThanSplittingOne()
    {
        var model = new ModelCapabilities("test", "test", PromptCharBudget: 20, true, true, false);

        string prompt = PromptBuilder.Build(null, new[] { "aaaaa", "bbbbb", "ccccc", "ddddd" }, model);

        Assert.Equal("aaaaa, bbbbb, ccccc", prompt);
    }

    [Fact]
    public void ParseVocabulary_SplitsOnLinesAndTrims()
    {
        List<string> terms = PromptBuilder.ParseVocabulary("Power BI\r\n  DAX  \n\nCopilot\n");

        Assert.Equal(new[] { "Power BI", "DAX", "Copilot" }, terms);
    }

    [Fact]
    public void ModelCapabilities_ForUnknownModel_FallsBackToTheGpt4oProfile()
    {
        ModelCapabilities capabilities = ModelCapabilities.For("gpt-transcribe-next");

        Assert.Equal("gpt-transcribe-next", capabilities.Id);
        Assert.False(capabilities.SupportsVerboseJson);
        Assert.Equal(2000, capabilities.PromptCharBudget);
    }
}
