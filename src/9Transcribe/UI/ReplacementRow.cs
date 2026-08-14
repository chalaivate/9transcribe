using CommunityToolkit.Mvvm.ComponentModel;
using NineTranscribe.Settings;

namespace NineTranscribe.UI;

/// <summary>One editable row of the find/replace grid on the vocabulary tab.</summary>
public sealed class ReplacementRow : ObservableObject
{
    private string _find = string.Empty;
    private string _replace = string.Empty;
    private bool _matchCase;

    public string Find
    {
        get => _find;
        set => SetProperty(ref _find, value ?? string.Empty);
    }

    public string Replace
    {
        get => _replace;
        set => SetProperty(ref _replace, value ?? string.Empty);
    }

    public bool MatchCase
    {
        get => _matchCase;
        set => SetProperty(ref _matchCase, value);
    }

    public static ReplacementRow From(ReplacementRuleSetting setting) => new()
    {
        Find = setting.Find ?? string.Empty,
        Replace = setting.Replace ?? string.Empty,
        MatchCase = setting.MatchCase,
    };

    public ReplacementRuleSetting ToSetting() => new()
    {
        Find = Find,
        Replace = Replace,
        MatchCase = MatchCase,
    };
}
