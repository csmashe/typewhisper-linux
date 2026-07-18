using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Runtime.InteropServices;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;
using TypeWhisper.Linux.Services;
using TypeWhisper.Linux.Services.Localization;

namespace TypeWhisper.Linux.ViewModels.Sections;

// MVVM Toolkit [ObservableProperty] generates the On<Property>Changed(value) partial hooks; the
// value parameter is part of the generated signature and cannot be dropped even when ignored here.
// ReSharper disable UnusedParameterInPartialMethod
public partial class AboutSectionViewModel : ObservableObject
{
    private readonly IErrorLogService _errorLog;
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

    // Active error-log category filter; null Key means "All categories".
    [ObservableProperty]
    private CategoryFilterOption? _selectedCategoryFilter;

    // Set while RefreshErrors rebuilds the filter list so reseating the selection
    // doesn't re-run ApplyFilter mid-rebuild (RefreshErrors applies it once at the end).
    private bool _suppressFilter;

    public AboutSectionViewModel(
        IErrorLogService errorLog,
        SettingsBackupService settingsBackup,
        UpdateCheckService updateCheck
    )
    {
        _errorLog = errorLog;
        _settingsBackup = settingsBackup;
        _updateCheck = updateCheck;
        RefreshErrors();
        // EntriesChanged fires synchronously on whichever thread called AddEntry —
        // and producers now log from background threads (transcription, detection,
        // plugin host). Marshal to the UI thread so refreshing the bound collections
        // can't throw a cross-thread mutation back into the producer's failure path.
        _errorLog.EntriesChanged += OnErrorEntriesChanged;

        _updateCheck.ResultChanged += OnUpdateResultChanged;
        // Reflect any check that already ran (e.g. the startup check).
        ApplyUpdateResult(_updateCheck.LastResult);
    }

    // Prefers AssemblyInformationalVersion (keeps pre-release suffix, strips +hash).
    // Shared with the update checker so both sides agree on "current".
    // ReSharper disable once ReplaceAutoPropertyWithComputedProperty -- kept as an instance auto-property; the computed form is flagged static (CA1822) on this bindable VM member.
    public string Version { get; } = AppVersion.Display;

    // Embedded at build time (Directory.Build.props); shows the upstream/Excel-on-the-Web split in-app.
    // ReSharper disable once ReplaceAutoPropertyWithComputedProperty -- kept as an instance auto-property; the computed form is flagged static (CA1822) on this bindable VM member.
    public string Copyright { get; } = AppVersion.Copyright;

    // ReSharper disable once UnusedMember.Global  public ViewModel property (About-section system-info display); not currently bound in-tree
    public string RuntimeVersion { get; } = Environment.Version.ToString();

    // ReSharper disable once UnusedMember.Global  public ViewModel property (About-section system-info display); not currently bound in-tree
    public string OsDescription { get; } =
        RuntimeInformation.OSDescription;

    // ReSharper disable once UnusedMember.Global  public ViewModel property (About-section system-info display); not currently bound in-tree
    public string Architecture { get; } =
        RuntimeInformation.OSArchitecture.ToString();

    // ReSharper disable once UnusedMember.Global  public ViewModel property (About-section project URL display); not currently bound in-tree
    // ReSharper disable once ReplaceAutoPropertyWithComputedProperty -- kept as an instance auto-property; the computed form is flagged static (CA1822) on this bindable VM member.
    public string ProjectUrl { get; } = "https://github.com/csmashe/typewhisper-linux";

    // ReSharper disable once UnusedMember.Global  public ViewModel property (About-section upstream URL display); not currently bound in-tree
    // ReSharper disable once ReplaceAutoPropertyWithComputedProperty -- kept as an instance auto-property; the computed form is flagged static (CA1822) on this bindable VM member.
    public string UpstreamUrl { get; } = "https://github.com/TypeWhisper/typewhisper-win";

    public bool CanCheckForUpdates => !IsCheckingForUpdates;

    // Full, unfiltered backing list; drives HasErrors and the category options.
    private ObservableCollection<ErrorLogEntry> ErrorEntries { get; } = [];

    // The entries actually shown — ErrorEntries narrowed by SelectedCategoryFilter.
    public ObservableCollection<ErrorLogEntry> FilteredErrorEntries { get; } = [];

    // "All categories" + one option per category currently present in the log.
    public ObservableCollection<CategoryFilterOption> CategoryFilters { get; } = [];

    public bool HasErrors => ErrorEntries.Count > 0;
    public bool HasVisibleErrors => FilteredErrorEntries.Count > 0;

    // Errors exist, but the active category filter hides all of them.
    public bool ShowEmptyCategoryNotice => HasErrors && !HasVisibleErrors;

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
            var result = await Task.Run(() => _settingsBackup.StageRestore(path));
            BackupStatusText =
                Loc.Instance.GetString("About.BackupStaged", result.FileCount);
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

    private void OnErrorEntriesChanged()
    {
        // Mirror OnUpdateResultChanged: hop to the UI thread before touching
        // ObservableCollections bound to the About view.
        if (Dispatcher.UIThread.CheckAccess())
        {
            RefreshErrors();
        }
        else
        {
            Dispatcher.UIThread.Post(RefreshErrors);
        }
    }

    private void RefreshErrors()
    {
        ErrorEntries.Clear();
        foreach (var entry in _errorLog.Entries)
        {
            ErrorEntries.Add(entry);
        }

        RebuildCategoryFilters();
        ApplyFilter();
        OnPropertyChanged(nameof(HasErrors));
    }

    private void RebuildCategoryFilters()
    {
        // Distinct categories present, sorted for a stable dropdown order, behind an
        // "All categories" option (null Key) that clears the filter.
        var present = ErrorEntries
            .Select(entry => entry.Category)
            .Distinct()
            .ToList();
        present.Sort(StringComparer.Ordinal);

        var desired = new List<CategoryFilterOption>
        {
            new(null, Loc.Instance["About.ErrorFilterAll"])
        };
        desired.AddRange(present.Select(c => new CategoryFilterOption(c, FormatCategory(c))));

        // Option set unchanged → keep the current selection object as-is.
        if (CategoryFilters.Select(o => o.Key).SequenceEqual(desired.Select(o => o.Key)))
        {
            return;
        }

        var previousKey = SelectedCategoryFilter?.Key;

        _suppressFilter = true;
        CategoryFilters.Clear();
        foreach (var option in desired)
        {
            CategoryFilters.Add(option);
        }

        // Preserve the user's selection across refreshes when its category still exists,
        // otherwise fall back to "All categories".
        SelectedCategoryFilter =
            CategoryFilters.FirstOrDefault(o => o.Key == previousKey) ?? CategoryFilters[0];
        _suppressFilter = false;
    }

    private void ApplyFilter()
    {
        var key = SelectedCategoryFilter?.Key;

        FilteredErrorEntries.Clear();
        foreach (var entry in ErrorEntries.Where(entry => key is null || entry.Category == key))
        {
            FilteredErrorEntries.Add(entry);
        }

        OnPropertyChanged(nameof(HasVisibleErrors));
        OnPropertyChanged(nameof(ShowEmptyCategoryNotice));
    }

    partial void OnSelectedCategoryFilterChanged(CategoryFilterOption? value)
    {
        if (_suppressFilter)
        {
            return;
        }

        ApplyFilter();
    }

    private static string FormatCategory(string category)
    {
        return string.IsNullOrEmpty(category)
            ? category
            : CultureInfo.CurrentCulture.TextInfo.ToTitleCase(category);
    }

    /// <summary>A selectable error-log category filter; a null <paramref name="Key" /> matches every entry.</summary>
    public sealed record CategoryFilterOption(string? Key, string Display)
    {
        // ComboBox renders items via ToString when no ItemTemplate is set.
        public override string ToString()
        {
            return Display;
        }
    }
}
