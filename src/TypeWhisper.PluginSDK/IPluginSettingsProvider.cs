namespace TypeWhisper.PluginSDK;

public interface IPluginSettingsProvider
{
    IReadOnlyList<PluginSettingDefinition> GetSettingDefinitions();
    Task<string?> GetSettingValueAsync(string key, CancellationToken ct = default);
    Task SetSettingValueAsync(string key, string? value, CancellationToken ct = default);
    Task<PluginSettingsValidationResult?> ValidateAsync(CancellationToken ct = default)
        => Task.FromResult<PluginSettingsValidationResult?>(null);
}

public enum PluginSettingKind
{
    Auto,
    Text,
    Secret,
    Dropdown,
    Boolean,
    Multiline
}

public sealed record PluginSettingDefinition(
    string Key,
    string Label,
    bool IsSecret = false,
    string? Placeholder = null,
    string? Description = null,
    IReadOnlyList<PluginSettingOption>? Options = null,
    PluginSettingKind Kind = PluginSettingKind.Auto);

public sealed record PluginSettingOption(
    string Value,
    string Label);

public sealed record PluginSettingsValidationResult(
    bool IsSuccess,
    string Message);
