using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TypeWhisper.Linux.Services;
using TypeWhisper.Linux.Services.Hotkey.DeSetup;
using TypeWhisper.Linux.Services.Insertion;
using TypeWhisper.Linux.Services.Localization;

namespace TypeWhisper.Linux.ViewModels.Sections;

// Façade over LinuxCapabilitySnapshot + YdotoolSetupHelper so the panel
// can bind status strings and run the one-click setup.
public partial class TextInsertionSectionViewModel : ObservableObject
{
    private readonly SystemCommandAvailabilityService _commands;
    private readonly YdotoolSetupHelper _setup;

    [ObservableProperty]
    private string _integrationStatusMessage = "";

    [ObservableProperty]
    private string _statusMessage = "";

    // Redraw the last status lines on a language switch. Setup results arrive already
    // worded by YdotoolSetupHelper, so those keep the language they were produced in.
    private Func<string>? _integrationStatus;
    private Func<string>? _status;

    public TextInsertionSectionViewModel(
        SystemCommandAvailabilityService commands,
        YdotoolSetupHelper setup
    )
    {
        _commands = commands;
        _setup = setup;
        Loc.Instance.LanguageChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(XdotoolStatusText));
            OnPropertyChanged(nameof(WtypeStatusText));
            OnPropertyChanged(nameof(YdotoolStatusText));
            OnPropertyChanged(nameof(SetupPreview));
            if (_integrationStatus is { } integrationStatus)
            {
                IntegrationStatusMessage = integrationStatus();
            }

            if (_status is { } status)
            {
                StatusMessage = status();
            }
        };
    }

    private void ShowIntegrationStatus(Func<string> render)
    {
        _integrationStatus = render;
        IntegrationStatusMessage = render();
    }

    private void ShowStatus(Func<string> render)
    {
        _status = render;
        StatusMessage = render();
    }

    private static string FormatSetupResult(YdotoolSetupHelper.SetupResult result) =>
        string.IsNullOrWhiteSpace(result.Detail) ? result.Message : $"{result.Message}\n{result.Detail}";

    private LinuxCapabilitySnapshot Snapshot => _commands.GetSnapshot();
    private YdotoolSetupHelper.Status YdotoolStatus => _setup.IsCurrentlyConfigured();

    public string SessionType => Snapshot.SessionType;
    // ReSharper disable once MemberCanBeMadeStatic.Global
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "XAML binding surface; ViewModel properties must be instance members for compiled bindings")]
    public string CompositorDisplayName => DesktopDetector.DisplayName();
    public string ClipboardToolStatus => Snapshot.ClipboardStatus;

    public bool CompositorRejectsWtype => Snapshot.CompositorRejectsWtype;

    // Hidden on X11, and hidden once fully configured with nothing left to act on.
    public bool ShowYdotoolSetup =>
        Snapshot.SessionType == "Wayland"
        && (ShowManualInstructions || CanSetUpAutomatically || CanRemoveIntegration);

    public string XdotoolStatusText
    {
        get
        {
            if (!Snapshot.HasXdotool)
            {
                return Loc.Instance["TextInsertion.NotInstalled"];
            }

            return Snapshot.SessionType == "Wayland"
                ? Loc.Instance["TextInsertion.XdotoolInstalledXWaylandOnly"]
                : Loc.Instance["TextInsertion.Installed"];
        }
    }

    public string XdotoolStatusTone =>
        Snapshot.HasXdotool ? Snapshot.SessionType == "Wayland" ? "warn" : "ok" : "missing";

    public string WtypeStatusText
    {
        get
        {
            if (!Snapshot.HasWtype)
            {
                return Loc.Instance["TextInsertion.NotInstalled"];
            }

            if (Snapshot.SessionType != "Wayland")
            {
                return Loc.Instance["TextInsertion.WtypeInstalledWaylandOnly"];
            }

            return Snapshot.CompositorRejectsWtype
                ? Loc.Instance["TextInsertion.WtypeUnsupportedCompositor"]
                : Loc.Instance["TextInsertion.Installed"];
        }
    }

    public string WtypeStatusTone =>
        !Snapshot.HasWtype ? "missing"
        : Snapshot.CompositorRejectsWtype || Snapshot.SessionType != "Wayland" ? "warn"
        : "ok";

    public string YdotoolStatusText
    {
        get
        {
            var status = YdotoolStatus;
            if (!status.BinaryInstalled)
            {
                return Loc.Instance["TextInsertion.YdotoolNotInstalled"];
            }

            // Only flag the missing udev rule when /dev/uinput isn't already
            // accessible — if the kernel grants it directly the rule isn't needed.
            if (status is { UdevRulePresent: false, UinputAccessible: false })
            {
                return Loc.Instance["TextInsertion.YdotoolUdevRuleMissing"];
            }

            if (!status.SystemdUnitActive)
            {
                return Loc.Instance["TextInsertion.YdotoolUnitInactive"];
            }

            if (!status.SocketReachable)
            {
                return Loc.Instance["TextInsertion.YdotoolSocketUnreachable"];
            }

            // Probe failed: daemon is up but /dev/uinput is unwritable (usually EACCES).
            // Showing "Ready" here would contradict the setup-result popup.
            return !status.ProbeSucceeded
                ? Loc.Instance["TextInsertion.YdotoolProbeFailed"]
                : Loc.Instance.GetString("TextInsertion.YdotoolReady", status.SocketPath);
        }
    }

    public string YdotoolStatusTone => YdotoolStatus.IsFullyConfigured ? "ok" : "missing";

    // ReSharper disable once MemberCanBeMadeStatic.Global
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "XAML binding surface; ViewModel properties must be instance members for compiled bindings")]
    public string SetupPreview => YdotoolSetupHelper.PreviewLines();

    public bool CanSetUpAutomatically =>
        YdotoolStatus is { BinaryInstalled: true, IsFullyConfigured: false };

    public bool ShowManualInstructions => !YdotoolStatus.BinaryInstalled;

    // Kept as a separate block so Remove stays reachable after a successful
    // setup (when CanSetUpAutomatically is false the setup panel would vanish).
    public bool CanRemoveIntegration =>
        YdotoolStatus.UdevRulePresent || YdotoolStatus.SystemdUnitActive;

    // ReSharper disable once MemberCanBeMadeStatic.Global
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "XAML binding surface; ViewModel properties must be instance members for compiled bindings")]
    public string ManualInstallCommand =>
        "Fedora:        sudo dnf install ydotool\n"
        + "Debian/Ubuntu: sudo apt install ydotool\n"
        + "Arch:          sudo pacman -S ydotool";

    [RelayCommand]
    private async Task SetUpYdotoolAsync()
    {
        ShowIntegrationStatus(() => Loc.Instance["TextInsertion.SettingUpYdotool"]);
        try
        {
            var result = await _setup.SetUpAsync(CancellationToken.None).ConfigureAwait(true);
            var text = FormatSetupResult(result);
            ShowIntegrationStatus(() => text);
        }
        catch (Exception ex)
        {
            var reason = ex.Message;
            ShowIntegrationStatus(() => Loc.Instance.GetString("TextInsertion.SetupFailed", reason));
        }
        finally
        {
            RefreshDerivedProperties();
        }
    }

    [RelayCommand]
    private async Task RemoveYdotoolAsync()
    {
        ShowIntegrationStatus(() => Loc.Instance["TextInsertion.RemovingYdotool"]);
        try
        {
            var result = await _setup.RemoveAsync(CancellationToken.None).ConfigureAwait(true);
            var text = FormatSetupResult(result);
            ShowIntegrationStatus(() => text);
        }
        catch (Exception ex)
        {
            var reason = ex.Message;
            ShowIntegrationStatus(() => Loc.Instance.GetString("TextInsertion.RemovalFailed", reason));
        }
        finally
        {
            RefreshDerivedProperties();
        }
    }

    [RelayCommand]
    private void RefreshStatus()
    {
        _commands.RefreshSnapshot();
        RefreshDerivedProperties();
        ShowStatus(() => Loc.Instance["TextInsertion.StatusRefreshed"]);
    }

    // LinuxCapabilitySnapshot is a value type rebuilt out-of-band and can't
    // auto-notify, so raise PropertyChanged manually after mutating actions.
    private void RefreshDerivedProperties()
    {
        OnPropertyChanged(nameof(SessionType));
        OnPropertyChanged(nameof(CompositorDisplayName));
        OnPropertyChanged(nameof(ClipboardToolStatus));
        OnPropertyChanged(nameof(CompositorRejectsWtype));
        OnPropertyChanged(nameof(ShowYdotoolSetup));
        OnPropertyChanged(nameof(XdotoolStatusText));
        OnPropertyChanged(nameof(XdotoolStatusTone));
        OnPropertyChanged(nameof(WtypeStatusText));
        OnPropertyChanged(nameof(WtypeStatusTone));
        OnPropertyChanged(nameof(YdotoolStatusText));
        OnPropertyChanged(nameof(YdotoolStatusTone));
        OnPropertyChanged(nameof(CanSetUpAutomatically));
        OnPropertyChanged(nameof(CanRemoveIntegration));
        OnPropertyChanged(nameof(ShowManualInstructions));
        OnPropertyChanged(nameof(SetupPreview));
    }
}