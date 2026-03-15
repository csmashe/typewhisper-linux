using System.Windows.Controls;

namespace TypeWhisper.Plugin.LiveTranscript;

/// <summary>
/// Settings view for the Live Transcript plugin.
/// Provides sliders for font size and window opacity.
/// </summary>
public partial class LiveTranscriptSettingsView : UserControl
{
    private readonly LiveTranscriptPlugin _plugin;
    private bool _initialized;

    public LiveTranscriptSettingsView(LiveTranscriptPlugin plugin)
    {
        _plugin = plugin;
        InitializeComponent();

        // Load current values from plugin settings
        FontSizeSlider.Value = plugin.FontSize;
        FontSizeLabel.Text = plugin.FontSize.ToString();

        OpacitySlider.Value = plugin.Opacity;
        OpacityLabel.Text = $"{(int)(plugin.Opacity * 100)}%";

        _initialized = true;
    }

    private void FontSizeSlider_ValueChanged(object sender, System.Windows.RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_initialized) return;

        var size = (int)e.NewValue;
        FontSizeLabel.Text = size.ToString();
        _plugin.FontSize = size;
    }

    private void OpacitySlider_ValueChanged(object sender, System.Windows.RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_initialized) return;

        var opacity = e.NewValue;
        OpacityLabel.Text = $"{(int)(opacity * 100)}%";
        _plugin.Opacity = opacity;
    }
}
