namespace TypeWhisper.Core.Interfaces;

/// <summary>
///     Resolves the names of items TypeWhisper creates itself (seeded prompts and profiles) in the
///     UI language. Services use shipped-name matching together with stable item identity and
///     persisted rename intent to preserve the user's names.
/// </summary>
public interface IDefaultNameLocalizer
{
    string GetName(string key);
    bool IsShippedName(string key, string name);
}
