using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;

namespace TypeWhisper.Linux.ViewModels.Sections;

public partial class PromptsSectionViewModel : ObservableObject
{
    private readonly IPromptActionService _prompts;

    public ObservableCollection<PromptAction> Actions { get; } = [];

    [ObservableProperty] private string _newName = "";
    [ObservableProperty] private string _newSystemPrompt = "";

    public PromptsSectionViewModel(IPromptActionService prompts)
    {
        _prompts = prompts;
        _prompts.ActionsChanged += () => Dispatcher.UIThread.Post(Refresh);
        Refresh();
    }

    private void Refresh()
    {
        Actions.Clear();
        foreach (var a in _prompts.Actions.OrderBy(a => a.SortOrder).ThenBy(a => a.Name))
            Actions.Add(a);
    }

    [RelayCommand]
    private void AddPrompt()
    {
        if (string.IsNullOrWhiteSpace(NewName) || string.IsNullOrWhiteSpace(NewSystemPrompt)) return;
        _prompts.AddAction(new PromptAction
        {
            Id = Guid.NewGuid().ToString(),
            Name = NewName.Trim(),
            SystemPrompt = NewSystemPrompt.Trim(),
            IsEnabled = true,
            IsPreset = false,
            SortOrder = Actions.Count,
        });
        NewName = "";
        NewSystemPrompt = "";
    }

    [RelayCommand]
    private void Delete(PromptAction p) => _prompts.DeleteAction(p.Id);

    [RelayCommand]
    private void ToggleEnabled(PromptAction p) => _prompts.UpdateAction(p with { IsEnabled = !p.IsEnabled });

    [RelayCommand]
    private void SeedPresets() => _prompts.SeedPresets();
}
