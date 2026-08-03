using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Collections.ObjectModel;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;
using TypeWhisper.Linux.Services;
using TypeWhisper.Linux.Services.Localization;

namespace TypeWhisper.Linux.ViewModels.Sections;

public partial class GeneralSectionViewModel : ObservableObject
{
    private readonly HttpApiService _api;
    private readonly CliInstallService _cliInstall;
    private readonly LinuxPreferencesService _linuxPrefs;
    private readonly ISettingsService _settings;
    private readonly TrayIconService _tray;
    private bool _updatingStartWithSystem;
    private bool _autostartStatusIsHint = true;

    // Set while Refresh hydrates from persisted settings so the generated On<Property>Changed
    // hooks don't write the value straight back. Delivery is deferred to the UI thread, so
    // without this a refresh could persist its snapshot over a newer commit.
    private bool _hydratingFromSettings;

    [ObservableProperty]
    private string _apiBearerToken = "";

    [ObservableProperty]
    private bool _apiServerEnabled;

    [ObservableProperty]
    private int _apiServerPort;

    [ObservableProperty]
    private string _apiStatusText = "";

    [ObservableProperty]
    private bool _cliBundledAvailable;

    [ObservableProperty]
    private string _cliBundledPathText = "";

    [ObservableProperty]
    private bool _cliInstalled;

    [ObservableProperty]
    private string _cliStatusText = "";

    [ObservableProperty]
    private bool _closeToTray;

    [ObservableProperty]
    private bool _startWithSystem;

    [ObservableProperty]
    private string _autostartStatusText = Loc.Instance["General.AutostartHint"];

    [ObservableProperty]
    private string? _uiLanguage;

    public GeneralSectionViewModel(
        ISettingsService settings,
        HttpApiService api,
        CliInstallService cliInstall,
        LinuxPreferencesService linuxPrefs,
        TrayIconService tray
    )
    {
        _settings = settings;
        _api = api;
        _cliInstall = cliInstall;
        _linuxPrefs = linuxPrefs;
        _tray = tray;
        Refresh(settings.Current);
        _startWithSystem = StartupService.IsEnabled;
        CloseToTray = _linuxPrefs.Current.CloseToTray;
        // Both fire on whichever thread wrote — a background Save, or the teardown continuation
        // left on the pool by the awaited model unload — so hop to the UI thread rather than
        // mutating bound properties off it. Re-read Current when the post runs instead of
        // capturing the payload, so queued refreshes coalesce onto the newest commit.
        _settings.SettingsChanged += _ =>
            Dispatcher.UIThread.Post(() => Refresh(_settings.Current));
        Loc.Instance.LanguageChanged += (_, _) =>
        {
            if (_autostartStatusIsHint)
            {
                AutostartStatusText = Loc.Instance["General.AutostartHint"];
            }
        };
        _api.StateChanged += () =>
            Dispatcher.UIThread.Post(() => ApiStatusText = _api.StatusText);
        ApiStatusText = _api.StatusText;
        RefreshCliState();
    }

    public ObservableCollection<CommandExample> CurlExamples { get; } = [];
    public ObservableCollection<CommandExample> CliExamples { get; } = [];

    // Only the languages we actually ship a JSON catalog for (plus "Auto"),
    // discovered at startup by Loc — no dead/un-translated choices.
    public IReadOnlyList<UiLanguageOption> UiLanguageChoices { get; } =
        Loc.Instance.AvailableUiLanguages;

    // ReSharper disable once MemberCanBeMadeStatic.Global
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "XAML binding surface; ViewModel properties must be instance members for compiled bindings")]
    public bool IsUiLanguageSupported => true;

    public UiLanguageOption? SelectedUiLanguageOption
    {
        get =>
            UiLanguageChoices.FirstOrDefault(option =>
                string.Equals(option.Code, UiLanguage, StringComparison.Ordinal)
            );
        set
        {
            var selected = value?.Code;
            if (string.Equals(selected, UiLanguage, StringComparison.Ordinal))
            {
                return;
            }

            UiLanguage = selected;
            OnPropertyChanged();
        }
    }

    // Read live (not cached): the tray probe is one-shot at startup and this
    // VM can be constructed before or after it. Without a tray the close-to-
    // tray toggle must be disabled — otherwise the window hides with no way
    // to bring it back (backlog #18).
    public bool IsTrayAvailable => _tray.IsTrayAvailable;

    public bool IsTrayUnavailable => !_tray.IsTrayAvailable;

    private void Refresh(AppSettings s)
    {
        _hydratingFromSettings = true;
        try
        {
            UiLanguage = s.UiLanguage;
            ApiServerEnabled = s.ApiServerEnabled;
            ApiServerPort = s.ApiServerPort;
            ApiBearerToken = HttpApiService.ReadBearerToken(s);
            RefreshExamples(s.ApiServerPort);
            OnPropertyChanged(nameof(SelectedUiLanguageOption));
        }
        finally
        {
            _hydratingFromSettings = false;
        }
    }

    [RelayCommand]
    private void RefreshCliState()
    {
        ApplyCliState(_cliInstall.GetState());
    }

    [RelayCommand]
    private void InstallCli()
    {
        try
        {
            ApplyCliState(_cliInstall.Install());
        }
        catch (Exception ex)
            when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            CliStatusText = ex.Message;
        }
    }

    private void ApplyCliState(CliInstallState state)
    {
        CliStatusText = state.StatusText;
        CliBundledAvailable = state.BundledCliAvailable;
        CliInstalled = state.Installed;
        CliBundledPathText = state.BundledPath is null
            ? Loc.Instance.GetString("General.CliInstallerTarget", state.LauncherPath)
            : Loc.Instance.GetString(
                "General.CliBundledTarget",
                state.BundledPath,
                state.LauncherPath
            );
    }

    private void RefreshExamples(int port)
    {
        CurlExamples.Clear();
        foreach (var command in CliInstallService.BuildCurlExamples(port))
        {
            CurlExamples.Add(new CommandExample(command));
        }

        CliExamples.Clear();
        foreach (var command in CliInstallService.BuildCliExamples(port))
        {
            CliExamples.Add(new CommandExample(command));
        }
    }

    partial void OnUiLanguageChanged(string? value)
    {
        // Applying the language still runs while hydrating; only the write-back is suppressed.
        if (!_hydratingFromSettings)
        {
            _settings.Save(_settings.Current with { UiLanguage = value });
        }

        Loc.Instance.CurrentLanguage = Loc.Instance.ResolveLanguage(value);
        OnPropertyChanged(nameof(SelectedUiLanguageOption));
    }

    partial void OnStartWithSystemChanged(bool value)
    {
        if (_updatingStartWithSystem)
        {
            return;
        }

        _updatingStartWithSystem = true;
        try
        {
            var result = value ? StartupService.Enable() : StartupService.Disable();
            AutostartStatusText = result.StatusText;
            _autostartStatusIsHint = result.Success;
            StartWithSystem = result.IsEnabled;
        }
        catch (Exception ex)
            when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            AutostartStatusText = ex.Message;
            _autostartStatusIsHint = false;
            StartWithSystem = StartupService.IsEnabled;
        }
        finally
        {
            _updatingStartWithSystem = false;
        }
    }

    partial void OnApiServerEnabledChanged(bool value)
    {
        if (_hydratingFromSettings || _settings.Current.ApiServerEnabled == value)
        {
            return;
        }

        _settings.Save(_settings.Current with { ApiServerEnabled = value });
    }

    partial void OnApiServerPortChanged(int value)
    {
        if (_hydratingFromSettings
            || value <= 0
            || value > 65535
            || _settings.Current.ApiServerPort == value)
        {
            return;
        }

        _settings.Save(_settings.Current with { ApiServerPort = value });
    }

    partial void OnCloseToTrayChanged(bool value)
    {
        if (_linuxPrefs.Current.CloseToTray == value)
        {
            return;
        }

        try
        {
            _linuxPrefs.Update(current => current with { CloseToTray = value });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A read-only or full disk must not throw out of a property setter into the binding.
            System.Diagnostics.Trace.WriteLine(
                $"[General] Could not persist the close-to-tray preference: {ex.Message}"
            );

            // The generated setter already published `value`, but the close handler reads
            // LinuxPreferences.Current, which a failed write leaves untouched — roll the toggle
            // back so it can't advertise behavior the app won't honor. The re-entrant call stops
            // at the equality guard above.
            CloseToTray = _linuxPrefs.Current.CloseToTray;
        }
    }
}

public sealed record CommandExample(string Command);
