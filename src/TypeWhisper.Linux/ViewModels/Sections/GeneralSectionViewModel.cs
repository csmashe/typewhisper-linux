using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Collections.ObjectModel;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;
using TypeWhisper.Linux.Services;

namespace TypeWhisper.Linux.ViewModels.Sections;

public partial class GeneralSectionViewModel : ObservableObject
{
    private readonly HttpApiService _api;
    private readonly CliInstallService _cliInstall;
    private readonly LinuxPreferencesService _linuxPrefs;
    private readonly ISettingsService _settings;
    private readonly TrayIconService _tray;

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
        StartWithSystem = StartupService.IsEnabled;
        CloseToTray = _linuxPrefs.Current.CloseToTray;
        _settings.SettingsChanged += Refresh;
        _api.StateChanged += () => ApiStatusText = _api.StatusText;
        ApiStatusText = _api.StatusText;
        RefreshCliState();
    }

    public ObservableCollection<CommandExample> CurlExamples { get; } = [];
    public ObservableCollection<CommandExample> CliExamples { get; } = [];

    public IReadOnlyList<UiLanguageOption> UiLanguageChoices { get; } =
    [
        new(null, "Auto (System)"),
        new("en", "English"),
        new("de", "Deutsch"),
        new("fr", "Français"),
        new("es", "Español"),
        new("pt", "Português"),
        new("ja", "日本語"),
        new("zh", "中文"),
        new("ko", "한국어"),
        new("it", "Italiano"),
        new("nl", "Nederlands"),
        new("pl", "Polski"),
        new("ru", "Русский")
    ];

    public bool IsUiLanguageSupported => false;

    public string UiLanguageSupportMessage =>
        "Interface language is not implemented in the Linux build yet.";

    public UiLanguageOption? SelectedUiLanguageOption
    {
        get =>
            UiLanguageChoices.FirstOrDefault(option =>
                string.Equals(option.Value, UiLanguage, StringComparison.Ordinal)
            );
        set
        {
            var selected = value?.Value;
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
        UiLanguage = s.UiLanguage;
        ApiServerEnabled = s.ApiServerEnabled;
        ApiServerPort = s.ApiServerPort;
        ApiBearerToken = HttpApiService.ReadBearerToken(s);
        RefreshExamples(s.ApiServerPort);
        OnPropertyChanged(nameof(SelectedUiLanguageOption));
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
            ? $"Installer target: {state.LauncherPath}"
            : $"Bundled: {state.BundledPath}  |  Target: {state.LauncherPath}";
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
        _settings.Save(_settings.Current with { UiLanguage = value });
        OnPropertyChanged(nameof(SelectedUiLanguageOption));
    }

    partial void OnStartWithSystemChanged(bool value)
    {
        if (value == StartupService.IsEnabled)
        {
            return;
        }

        if (value)
        {
            StartupService.Enable();
        }
        else
        {
            StartupService.Disable();
        }
    }

    partial void OnApiServerEnabledChanged(bool value)
    {
        if (_settings.Current.ApiServerEnabled == value)
        {
            return;
        }

        _settings.Save(_settings.Current with { ApiServerEnabled = value });
    }

    partial void OnApiServerPortChanged(int value)
    {
        if (value <= 0 || value > 65535 || _settings.Current.ApiServerPort == value)
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

        _linuxPrefs.Save(_linuxPrefs.Current with { CloseToTray = value });
    }
}

public sealed record UiLanguageOption(string? Value, string DisplayName);

public sealed record CommandExample(string Command);