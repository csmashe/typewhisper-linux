using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Collections.ObjectModel;
using System.Runtime.InteropServices;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;
using TypeWhisper.Linux.Services;
using TypeWhisper.Linux.Services.Localization;

namespace TypeWhisper.Linux.ViewModels.Sections;

public partial class AboutSectionViewModel : ObservableObject
{
    private readonly IErrorLogService _errorLog;
    private readonly LinuxPreferencesService _linuxPreferences;
    private readonly ISettingsService _settings;
    private readonly SettingsBackupService _settingsBackup;
    private readonly UpdateCheckService _updateCheck;

    [ObservableProperty]
    private string _backupStatusText = Loc.Instance["About.BackupStatusDefault"];

    [ObservableProperty]
    private bool _isBackupBusy;

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(CanCheckForUpdates))]
    private bool _isCheckingForUpdates;

    [ObservableProperty]
    private string? _latestReleaseUrl;

    [ObservableProperty]
    private bool _updateAvailable;

    [ObservableProperty]
    private string _updateStatusText = Loc.Instance["About.UpdateStatusDefault"];

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
        // Reflect any check that already ran (e.g. the startup check).
        ApplyUpdateResult(_updateCheck.LastResult);
    }

    // Prefers AssemblyInformationalVersion (keeps pre-release suffix, strips +hash).
    // Shared with the update checker so both sides agree on "current".
    public string Version { get; } = AppVersion.Display;

    // Embedded at build time (Directory.Build.props); shows the upstream/Excel-on-the-Web split in-app.
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
        BackupStatusText = Loc.Instance["About.CreatingBackup"];
        try
        {
            var result = await Task.Run(() => _settingsBackup.CreateBackup(path));
            BackupStatusText =
                Loc.Instance.GetString("About.BackupCreated", result.FileCount);
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
        BackupStatusText = Loc.Instance["About.RestoringBackup"];
        try
        {
            var result = await Task.Run(() => _settingsBackup.RestoreBackup(path));
            // Re-load and re-save each settings file so in-memory state
            // reflects the just-restored files and SettingsChanged is fired.
            _settings.Save(_settings.Load());
            _linuxPreferences.Save(_linuxPreferences.Load());
            BackupStatusText =
                Loc.Instance.GetString("About.BackupRestored", result.FileCount);
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
        UpdateStatusText = Loc.Instance["About.CheckingForUpdates"];
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
                Loc.Instance["About.UpdateCheckFailed"];
            return;
        }

        if (result.UpdateAvailable)
        {
            UpdateAvailable = true;
            LatestReleaseUrl = result.ReleaseUrl;
            UpdateStatusText =
                Loc.Instance.GetString("About.NewVersionAvailable", result.LatestVersion, result.CurrentVersion);
            return;
        }

        UpdateAvailable = false;
        LatestReleaseUrl = result.ReleaseUrl;

        // Distinguish "on latest" from "ahead of latest" — a dev build shouldn't
        // claim it is the latest published release.
        UpdateStatusText = AppVersion.Compare(result.CurrentVersion, result.LatestVersion) > 0
            ? Loc.Instance.GetString("About.NewerThanLatest", result.CurrentVersion, result.LatestVersion)
            : Loc.Instance.GetString("About.OnLatestVersion", result.CurrentVersion);
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