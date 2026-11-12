using TypeWhisper.Core.Interfaces;

namespace TypeWhisper.Linux.Services.Localization;

/// <summary>Names seeded prompts and profiles from the UI catalogs.</summary>
public sealed class LocDefaultNameLocalizer : IDefaultNameLocalizer
{
    private LocDefaultNameLocalizer() { }

    public static LocDefaultNameLocalizer Instance { get; } = new();

    public string GetName(string key) => Loc.Instance[key];

    public bool IsShippedName(string key, string name) => Loc.Instance.IsTranslationOf(key, name);
}
