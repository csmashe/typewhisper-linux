using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Collections.ObjectModel;
using System.Runtime.InteropServices;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;
using TypeWhisper.Linux.Services;

namespace TypeWhisper.Linux.ViewModels.Sections;

public partial class AboutSectionViewModel : ObservableObject
{
    private readonly IErrorLogService _errorLog;
    private readonly LinuxPreferencesService _linuxPreferences;
    private readonly ISettingsService _settings;
    private readonly SettingsBackupService _settingsBackup;
    private readonly UpdateCheckService _updateCheck;

    [ObservableProperty]
    private string _backupStatusText = "Back up settings, profiles, snippets, and plugin data.";

    [ObservableProperty]
    private bool _isBackupBusy;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanCheckForUpdates))]
    private bool _isCheckingForUpdates;

    [ObservableProperty]
    private string _updateStatusText = "Check for the latest TypeWhisper for Linux release.";

    [ObservableProperty]
    private bool _updateAvailable;

    [ObservableProperty]
    private string? _latestReleaseUrl;

    public AboutSectionViewModel(
        IErrorLogService errorLog,
        ISettingsService settings,
        LinuxPreferencesService linuxPreferences,
        SettingsBackupService settingsBackup,
        UpdateCheckService updateCheck
    )
    {
        _errorLog = errorLog;
        _settings = settings;
        _linuxPreferences = linuxPreferences;
        _settingsBackup = settingsBackup;
        _updateCheck = updateCheck;
        RefreshErrors();
        _errorLog.EntriesChanged += RefreshErrors;

        _updateCheck.ResultChanged += OnUpdateResultChanged;
        // Reflect any check that already ran (e.g. the startup check) when the
        // user first navigates to this section.
        ApplyUpdateResult(_updateCheck.LastResult);
    }

    // Resolved once in AppVersion: prefers AssemblyInformationalVersion so a
    // pre-release suffix ("-rc.1") survives and the +hash SourceLink suffix is
    // trimmed. Shared with the update checker so both agree on "current".
    public string Version { get; } = AppVersion.Display;

    // Copyright string embedded at build time (Directory.Build.props). Shown
    // on the About screen so the upstream/Excel-on-the-Web split is visible
    // in-app, not just in the NOTICE/LICENSE files.
    public string Copyright { get; } = AppVersion.Copyright;

    public string RuntimeVersion { get; } = Environment.Version.ToString();

    public string OsDescription { get; } =
        RuntimeInformation.OSDescription;

    public string Architecture { get; } =
        RuntimeInformation.OSArchitecture.ToString();

    public string ProjectUrl { get; } = "https://github.com/csmashe/typewhisper-linux";

    public string UpstreamUrl { get; } = "https://github.com/TypeWhisper/typewhisper-win";

    public bool CanCheckForUpdates => !IsCheckingForUpdates;

    public ObservableCollection<ErrorLogEntry> ErrorEntries { get; } = [];
    public bool HasErrors => ErrorEntries.Count > 0;

    public string ExportDiagnostics()
    {
        return _errorLog.ExportDiagnostics();
    }

    public async Task<SettingsBackupResult> CreateSettingsBackupAsync(string path)
    {
        if (IsBackupBusy)
        {
            throw new InvalidOperationException("A settings backup or restore is already running.");
        }

        IsBackupBusy = true;
        BackupStatusText = "Creating settings backup...";
        try
        {
            var result = await Task.Run(() => _settingsBackup.CreateBackup(path));
            BackupStatusText =
                $"Backup created with {result.FileCount} file(s). Models, audio, logs, and plugin binaries were skipped.";
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
        {
            throw new InvalidOperationException("A settings backup or restore is already running.");
        }

        IsBackupBusy = true;
        BackupStatusText = "Restoring settings backup...";
        try
        {
            var result = await Task.Run(() => _settingsBackup.RestoreBackup(path));
            // Re-load and re-save each settings file so in-memory state
            // reflects the just-restored files and SettingsChanged is fired.
            _settings.Save(_settings.Load());
            _linuxPreferences.Save(_linuxPreferences.Load());
            BackupStatusText =
                $"Backup restored from {result.FileCount} file(s). Some restored settings may require an app restart.";
            return result;
        }
        finally
        {
            IsBackupBusy = false;
        }
    }

    [RelayCommand]
    private async Task CheckForUpdatesAsync()
    {
        if (IsCheckingForUpdates)
        {
            return;
        }

        IsCheckingForUpdates = true;
        UpdateStatusText = "Checking for updates...";
        try
        {
            var result = await _updateCheck.CheckAsync();
            ApplyUpdateResult(result);
        }
        finally
        {
            IsCheckingForUpdates = false;
        }
    }

    [RelayCommand]
    private void OpenReleasePage()
    {
        UrlLauncher.Open(LatestReleaseUrl);
    }

    private void OnUpdateResultChanged(UpdateCheckResult result)
    {
        // The startup check raises this from a background thread.
        if (Dispatcher.UIThread.CheckAccess())
        {
            ApplyUpdateResult(result);
        }
        else
        {
            Dispatcher.UIThread.Post(() => ApplyUpdateResult(result));
        }
    }

    private void ApplyUpdateResult(UpdateCheckResult result)
    {
        if (!result.Checked)
        {
            return;
        }

        if (result.Faulted)
        {
            UpdateAvailable = false;
            LatestReleaseUrl = null;
            UpdateStatusText =
                "Couldn't check for updates right now. Try again later or visit the releases page.";
            return;
        }

        if (result.UpdateAvailable)
        {
            UpdateAvailable = true;
            LatestReleaseUrl = result.ReleaseUrl;
            UpdateStatusText =
                $"A new version is available: v{result.LatestVersion} (you have v{result.CurrentVersion}).";
            return;
        }

        UpdateAvailable = false;
        LatestReleaseUrl = result.ReleaseUrl;

        // Distinguish "exactly on the latest release" from "ahead of it" — a
        // dev/unreleased build (e.g. running v0.5.0 when v0.4.0 is the newest
        // published release) shouldn't claim it *is* the latest release.
        UpdateStatusText = AppVersion.Compare(result.CurrentVersion, result.LatestVersion) > 0
            ? $"You're running v{result.CurrentVersion}, which is newer than the latest published release (v{result.LatestVersion})."
            : $"You're on the latest version (v{result.CurrentVersion}).";
    }

    [RelayCommand]
    private void ClearErrors()
    {
        _errorLog.ClearAll();
        RefreshErrors();
    }

    private void RefreshErrors()
    {
        ErrorEntries.Clear();
        foreach (var entry in _errorLog.Entries)
        {
            ErrorEntries.Add(entry);
        }

        OnPropertyChanged(nameof(HasErrors));
    }
}