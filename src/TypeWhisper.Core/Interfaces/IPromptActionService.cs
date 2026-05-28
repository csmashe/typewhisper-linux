using TypeWhisper.Core.Models;

namespace TypeWhisper.Core.Interfaces;

public interface IPromptActionService
{
    IReadOnlyList<PromptAction> Actions { get; }
    IReadOnlyList<PromptAction> EnabledActions { get; }

    void AddAction(PromptAction action);
    void UpdateAction(PromptAction action);
    void DeleteAction(string id);
    void Reorder(IReadOnlyList<string> orderedIds);
    void SeedPresets();
    event Action? ActionsChanged;
}