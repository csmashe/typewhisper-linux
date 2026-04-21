using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;

namespace TypeWhisper.Linux.ViewModels.Sections;

public partial class ProfilesSectionViewModel : ObservableObject
{
    private readonly IProfileService _profiles;

    public ObservableCollection<Profile> Profiles { get; } = [];

    [ObservableProperty] private string _newName = "";
    [ObservableProperty] private string _newProcessNames = "";

    public ProfilesSectionViewModel(IProfileService profiles)
    {
        _profiles = profiles;
        _profiles.ProfilesChanged += () => Dispatcher.UIThread.Post(Refresh);
        Refresh();
    }

    private void Refresh()
    {
        Profiles.Clear();
        foreach (var p in _profiles.Profiles.OrderBy(p => p.Priority).ThenBy(p => p.Name))
            Profiles.Add(p);
    }

    [RelayCommand]
    private void AddProfile()
    {
        if (string.IsNullOrWhiteSpace(NewName)) return;
        var procs = NewProcessNames
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToArray();
        _profiles.AddProfile(new Profile
        {
            Id = Guid.NewGuid().ToString(),
            Name = NewName.Trim(),
            IsEnabled = true,
            Priority = 0,
            ProcessNames = procs,
            UrlPatterns = [],
        });
        NewName = "";
        NewProcessNames = "";
    }

    [RelayCommand]
    private void Delete(Profile p) => _profiles.DeleteProfile(p.Id);

    [RelayCommand]
    private void ToggleEnabled(Profile p) => _profiles.UpdateProfile(p with { IsEnabled = !p.IsEnabled });
}
