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

        if (_viewModel.HasVisibleContent)
        {
            if (!IsVisible)
                Show();

            Dispatcher.UIThread.Post(PositionOverlay, DispatcherPriority.Loaded);
        }
        else if (IsVisible)
        {
            Hide();
        }
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
