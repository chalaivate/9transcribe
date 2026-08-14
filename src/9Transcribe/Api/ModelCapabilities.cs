namespace NineTranscribe.Api;

/// <summary>What a transcription model accepts, so the client never sends a rejected field.</summary>
public sealed record ModelCapabilities(
    string Id,
    string DisplayName,
    int PromptCharBudget,
    bool SupportsPrompt,
    bool SupportsLanguage,
    bool SupportsVerboseJson)
{
    /// <summary>The three models offered in the settings picker, in menu order.</summary>
    public static readonly IReadOnlyList<ModelCapabilities> Known = new[]
    {
        // gpt-4o models accept only json/text for response_format; verbose_json is a 400.
        new ModelCapabilities(
            "gpt-4o-transcribe",
            "gpt-4o-transcribe (แม่นยำที่สุด — แนะนำ)",
            PromptCharBudget: 2000,
            SupportsPrompt: true,
            SupportsLanguage: true,
            SupportsVerboseJson: false),
        new ModelCapabilities(
            "gpt-4o-mini-transcribe",
            "gpt-4o-mini-transcribe (เร็วและประหยัดกว่า)",
            PromptCharBudget: 2000,
            SupportsPrompt: true,
            SupportsLanguage: true,
            SupportsVerboseJson: false),
        // whisper-1 only reads the final 224 tokens of the prompt; Thai is token-heavy, so
        // the character budget is deliberately far below what 224 tokens could hold.
        new ModelCapabilities(
            "whisper-1",
            "whisper-1 (รุ่นคลาสสิก)",
            PromptCharBudget: 500,
            SupportsPrompt: true,
            SupportsLanguage: true,
            SupportsVerboseJson: true),
    };

    /// <summary>
    /// Capabilities for <paramref name="modelId"/>. Unknown ids (the model field is free
    /// text so a newly released model can be used immediately) get the conservative
    /// gpt-4o profile.
    /// </summary>
    public static ModelCapabilities For(string? modelId)
    {
        if (!string.IsNullOrWhiteSpace(modelId))
        {
            foreach (ModelCapabilities candidate in Known)
            {
                if (string.Equals(candidate.Id, modelId, StringComparison.OrdinalIgnoreCase))
                {
                    return candidate;
                }
            }
        }

        string id = string.IsNullOrWhiteSpace(modelId) ? "gpt-4o-transcribe" : modelId.Trim();
        return new ModelCapabilities(
            id,
            id,
            PromptCharBudget: 2000,
            SupportsPrompt: true,
            SupportsLanguage: true,
            SupportsVerboseJson: false);
    }
}
