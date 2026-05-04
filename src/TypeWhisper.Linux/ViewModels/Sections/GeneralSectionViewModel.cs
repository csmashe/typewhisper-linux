using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;
using TypeWhisper.Linux.Services;

namespace TypeWhisper.Linux.ViewModels.Sections;

public partial class GeneralSectionViewModel : ObservableObject
{
    private readonly ISettingsService _settings;
    private readonly HttpApiService _api;
    private readonly CliInstallService _cliInstall;
    [ObservableProperty] private string? _uiLanguage;
    [ObservableProperty] private bool _startWithSystem;
    [ObservableProperty] private bool _apiServerEnabled;
    [ObservableProperty] private int _apiServerPort;
    [ObservableProperty] private string _apiBearerToken = "";
    [ObservableProperty] private string _apiStatusText = "";
    [ObservableProperty] private string _cliStatusText = "";
    [ObservableProperty] private string _cliBundledPathText = "";
    [ObservableProperty] private bool _cliBundledAvailable;
    [ObservableProperty] private bool _cliInstalled;

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
    public string UiLanguageSupportMessage => "Interface language is not implemented in the Linux build yet.";

    public UiLanguageOption? SelectedUiLanguageOption
    {
        get => UiLanguageChoices.FirstOrDefault(option =>
            string.Equals(option.Value, UiLanguage, StringComparison.Ordinal));
        set
        {
            var selected = value?.Value;
            if (string.Equals(selected, UiLanguage, StringComparison.Ordinal))
                return;

            UiLanguage = selected;
            OnPropertyChanged();
        }
    }

    public GeneralSectionViewModel(ISettingsService settings, HttpApiService api, CliInstallService cliInstall)
    {
        _settings = settings;
        _api = api;
        _cliInstall = cliInstall;
        Refresh(settings.Current);
        StartWithSystem = StartupService.IsEnabled;
        _settings.SettingsChanged += Refresh;
        _api.StateChanged += () => ApiStatusText = _api.StatusText;
        ApiStatusText = _api.StatusText;
        RefreshCliState();
    }

    private void Refresh(Core.Models.AppSettings s)
    {
        UiLanguage = s.UiLanguage;
        ApiServerEnabled = s.ApiServerEnabled;
        ApiServerPort = s.ApiServerPort;
        ApiBearerToken = HttpApiService.ReadBearerToken(s);
        RefreshExamples(s.ApiServerPort);
        OnPropertyChanged(nameof(SelectedUiLanguageOption));
    }

    [RelayCommand]
    private void RefreshCliState() => ApplyCliState(_cliInstall.GetState());

    [RelayCommand]
    private void InstallCli()
    {
        try
        {
            ApplyCliState(_cliInstall.Install());
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
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
            CurlExamples.Add(new CommandExample(command));

        CliExamples.Clear();
        foreach (var command in CliInstallService.BuildCliExamples(port))
            CliExamples.Add(new CommandExample(command));
    }

    partial void OnUiLanguageChanged(string? value)
    {
        _settings.Save(_settings.Current with { UiLanguage = value });
        OnPropertyChanged(nameof(SelectedUiLanguageOption));
    }

    partial void OnStartWithSystemChanged(bool value)
    {
        if (value == StartupService.IsEnabled)
            return;

        if (value)
            StartupService.Enable();
        else
            StartupService.Disable();
    }

    partial void OnApiServerEnabledChanged(bool value)
    {
        if (_settings.Current.ApiServerEnabled == value)
            return;

        _settings.Save(_settings.Current with { ApiServerEnabled = value });
    }

    partial void OnApiServerPortChanged(int value)
    {
        if (value <= 0 || value > 65535 || _settings.Current.ApiServerPort == value)
            return;

        _settings.Save(_settings.Current with { ApiServerPort = value });
    }
}

public sealed record UiLanguageOption(string? Value, string DisplayName);

public sealed record CommandExample(string Command);
