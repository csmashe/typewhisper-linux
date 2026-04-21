using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;

namespace TypeWhisper.Linux.ViewModels.Sections;

public partial class SnippetsSectionViewModel : ObservableObject
{
    private readonly ISnippetService _snippets;

    public ObservableCollection<Snippet> Snippets { get; } = [];

    [ObservableProperty] private string _newTrigger = "";
    [ObservableProperty] private string _newReplacement = "";
    [ObservableProperty] private bool _caseSensitive;

    public SnippetsSectionViewModel(ISnippetService snippets)
    {
        _snippets = snippets;
        _snippets.SnippetsChanged += () => Dispatcher.UIThread.Post(Refresh);
        Refresh();
    }

    private void Refresh()
    {
        Snippets.Clear();
        foreach (var s in _snippets.Snippets.OrderBy(s => s.Trigger, StringComparer.OrdinalIgnoreCase))
            Snippets.Add(s);
    }

    [RelayCommand]
    private void AddSnippet()
    {
        if (string.IsNullOrWhiteSpace(NewTrigger) || string.IsNullOrWhiteSpace(NewReplacement)) return;
        _snippets.AddSnippet(new Snippet
        {
            Id = Guid.NewGuid().ToString(),
            Trigger = NewTrigger.Trim(),
            Replacement = NewReplacement.Trim(),
            CaseSensitive = CaseSensitive,
            IsEnabled = true,
        });
        NewTrigger = "";
        NewReplacement = "";
    }

    [RelayCommand]
    private void Delete(Snippet s) => _snippets.DeleteSnippet(s.Id);

    [RelayCommand]
    private void ToggleEnabled(Snippet s) => _snippets.UpdateSnippet(s with { IsEnabled = !s.IsEnabled });
}
