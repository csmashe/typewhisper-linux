using TypeWhisper.Core.Models;

namespace TypeWhisper.Core.Interfaces;

/// <summary>
///     Stores the user's LLM prompt actions (text transforms) and their display order.
/// </summary>
public interface IPromptActionService
{
    IReadOnlyList<PromptAction> Actions { get; }

    /// <summary>Reads the actions and reports whether they are stored data.</summary>
    /// <returns><c>false</c> when the store was reset after corruption, not merely empty.</returns>
    bool TryGetActions(out IReadOnlyList<PromptAction> actions)
    {
        actions = Actions;
        return true;
    }

    /// <summary>The enabled subset of <see cref="Actions" />, in display order.</summary>
    IReadOnlyList<PromptAction> EnabledActions { get; }

    void AddAction(PromptAction action);
    void UpdateAction(PromptAction action);
    void DeleteAction(string id);

    /// <summary>Reorders the actions to match the given sequence of ids.</summary>
    void Reorder(IReadOnlyList<string> orderedIds);

    /// <summary>Adds the built-in preset actions when none are present (idempotent).</summary>
    void SeedPresets();

    /// <summary>Seeds default actions only on a genuine first run (when no actions file exists yet).</summary>
    void SeedFirstRunDefaultsIfMissing();

    event Action? ActionsChanged;
}
