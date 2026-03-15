using System.Windows;

namespace TypeWhisper.Plugin.LiveTranscript;

/// <summary>
/// Floating transparent window that displays live transcription text.
/// Positioned at the bottom center of the primary screen.
/// </summary>
public partial class LiveTranscriptWindow : Window
{
    public LiveTranscriptWindow()
    {
        InitializeComponent();
    }

    /// <summary>Gets the current displayed text.</summary>
    public string CurrentText => TranscriptText.Text;

    /// <summary>Updates the displayed transcript text and scrolls to the bottom.</summary>
    public void UpdateText(string text)
    {
        TranscriptText.Text = text;
        TranscriptScroller.ScrollToEnd();
    }

    /// <summary>Sets the font size of the transcript text.</summary>
    public void SetFontSize(int size)
    {
        TranscriptText.FontSize = size;
    }

    /// <summary>Sets the background opacity of the window border.</summary>
    public void SetWindowOpacity(double opacity)
    {
        RootBorder.Opacity = opacity;
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        PositionAtBottomCenter();
    }

    private void PositionAtBottomCenter()
    {
        var workArea = SystemParameters.WorkArea;
        Left = (workArea.Width - Width) / 2 + workArea.Left;
        Top = workArea.Bottom - ActualHeight - 40;
    }

    /// <summary>
    /// Re-position when size changes (SizeToContent="Height" may change actual height).
    /// </summary>
    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        if (IsLoaded)
            PositionAtBottomCenter();
    }
}
