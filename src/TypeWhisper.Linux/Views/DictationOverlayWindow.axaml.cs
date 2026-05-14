using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;
using Avalonia.Threading;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;
using TypeWhisper.Linux.ViewModels;

namespace TypeWhisper.Linux.Views;

public partial class DictationOverlayWindow : Window
{
    private readonly DictationOverlayViewModel? _viewModel;
    private readonly ISettingsService? _settings;

    public DictationOverlayWindow()
    {
        InitializeComponent();
    }

    public DictationOverlayWindow(DictationOverlayViewModel viewModel, ISettingsService settings)
        : this()
    {
        DataContext = _viewModel = viewModel;
        _settings = settings;

        ShowInTaskbar = false;
        ShowActivated = false;
        CanResize = false;
        Topmost = true;

        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        _settings.SettingsChanged += _ => Dispatcher.UIThread.Post(PositionOverlay);

        Opened += (_, _) => PositionOverlay();
        SizeChanged += (_, _) => PositionOverlay();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(DictationOverlayViewModel.HasVisibleContent))
            return;

        Dispatcher.UIThread.Post(UpdateWindowVisibility);
    }

    public void Initialize()
    {
        if (_viewModel is null)
            return;

        UpdateWindowVisibility();
    }

    private void UpdateWindowVisibility()
    {
        if (_viewModel is null)
            return;

        // WORKAROUND (docs/plans/2026-05-13-linux-backlog.md item 16):
        // Show() once and never Hide() — on Wayland with
        // ShowActivated="False" / Topmost="True" / ShowInTaskbar="False",
        // Avalonia's Window.Show() after a prior Hide() is unreliable
        // on GNOME Mutter: some shows succeed, some leave the window
        // invisible until the app is restarted. The recording overlay
        // would appear for the first one or two dictations and then
        // disappear permanently even though dictation kept working.
        // Driving visibility via Opacity keeps the window alive
        // throughout the app's lifetime and avoids the race entirely.
        // The inner Border bindings (IsVisible="{Binding ...}") still
        // handle which content (if any) is drawn, and a fully
        // transparent surface is essentially free on modern Wayland
        // compositors.
        //
        // Revisit if Avalonia ships a fix for Show()-after-Hide() on
        // Wayland utility windows, or if we switch to recreating the
        // overlay window per-dictation.
        var hasContent = _viewModel.HasVisibleContent;

        if (!IsVisible)
            Show();

        Opacity = hasContent ? 1.0 : 0.0;
        IsHitTestVisible = hasContent;

        if (hasContent)
            Dispatcher.UIThread.Post(PositionOverlay, DispatcherPriority.Loaded);
    }

    private void PositionOverlay()
    {
        if (!IsVisible || _settings is null)
            return;

        var screen = Screens?.Primary;
        if (screen is null)
            return;

        var workArea = screen.WorkingArea;
        var width = Math.Max(320, Bounds.Width);
        var height = Math.Max(56, Bounds.Height);
        var x = workArea.X + (workArea.Width - (int)Math.Ceiling(width)) / 2;
        var y = _settings.Current.OverlayPosition == OverlayPosition.Top
            ? workArea.Y + 12
            : workArea.Bottom - (int)Math.Ceiling(height) - 12;

        Position = new PixelPoint(x, y);
    }
}
