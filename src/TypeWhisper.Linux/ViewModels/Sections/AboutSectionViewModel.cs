using System.Collections.ObjectModel;
using System.Reflection;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;
using TypeWhisper.Linux.Services;

namespace TypeWhisper.Linux.ViewModels.Sections;

public partial class AboutSectionViewModel : ObservableObject
{
    private readonly IErrorLogService _errorLog;
    private readonly ISettingsService _settings;
    private readonly LinuxPreferencesService _linuxPreferences;
    private readonly SettingsBackupService _settingsBackup;

    public string Version { get; } =
        Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "dev";

    public string RuntimeVersion { get; } = Environment.Version.ToString();

    public string OsDescription { get; } = System.Runtime.InteropServices.RuntimeInformation.OSDescription;

    public string Architecture { get; } =
        System.Runtime.InteropServices.RuntimeInformation.OSArchitecture.ToString();

    public string ProjectUrl { get; } = "https://github.com/csmashe/typewhisper-linux";

    public string UpstreamUrl { get; } = "https://github.com/TypeWhisper/typewhisper-win";

    public bool CanCheckForUpdates => false;

    public string UpdateStatusText => "Automatic updates are not configured in this Linux build yet.";

    public ObservableCollection<ErrorLogEntry> ErrorEntries { get; } = [];
    public bool HasErrors => ErrorEntries.Count > 0;

    [ObservableProperty] private bool _isBackupBusy;
    [ObservableProperty] private string _backupStatusText = "Back up settings, profiles, snippets, and plugin data.";

    public AboutSectionViewModel(
        IErrorLogService errorLog,
        ISettingsService settings,
        LinuxPreferencesService linuxPreferences,
        SettingsBackupService settingsBackup)
    {
        _errorLog = errorLog;
        _settings = settings;
        _linuxPreferences = linuxPreferences;
        _settingsBackup = settingsBackup;
        RefreshErrors();
        _errorLog.EntriesChanged += RefreshErrors;
    }

    [RelayCommand]
    private void ClearErrors()
    {
        _errorLog.ClearAll();
        RefreshErrors();
    }

    public string ExportDiagnostics() => _errorLog.ExportDiagnostics();

    public async Task<SettingsBackupResult> CreateSettingsBackupAsync(string path)
    {
        if (IsBackupBusy)
            throw new InvalidOperationException("A settings backup or restore is already running.");

        IsBackupBusy = true;
        BackupStatusText = "Creating settings backup...";
        try
        {
            var result = await Task.Run(() => _settingsBackup.CreateBackup(path));
            BackupStatusText = $"Backup created with {result.FileCount} file(s). Models, audio, logs, and plugin binaries were skipped.";
            return result;
        }
        finally
        {
            IsBackupBusy = false;
        }
    }

    public async Task<SettingsBackupResult> RestoreSettingsBackupAsync(string path)
    {
        if (IsBackupBusy)
            throw new InvalidOperationException("A settings backup or restore is already running.");

        IsBackupBusy = true;
        BackupStatusText = "Restoring settings backup...";
        try
        {
            var result = await Task.Run(() => _settingsBackup.RestoreBackup(path));
            _settings.Save(_settings.Load());
            _linuxPreferences.Save(_linuxPreferences.Load());
            BackupStatusText = $"Backup restored from {result.FileCount} file(s). Some restored settings may require an app restart.";
            return result;
        }
        finally
        {
            IsBackupBusy = false;
        }
    }

    private void RefreshErrors()
    {
        ErrorEntries.Clear();
        foreach (var entry in _errorLog.Entries)
            ErrorEntries.Add(entry);

        OnPropertyChanged(nameof(HasErrors));
    }
}
