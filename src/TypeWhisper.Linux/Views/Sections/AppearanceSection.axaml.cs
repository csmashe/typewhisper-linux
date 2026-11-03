using Avalonia;
using Avalonia.Controls;
using TypeWhisper.Linux.ViewModels.Sections;

namespace TypeWhisper.Linux.Views.Sections;

public partial class AppearanceSection : UserControl
{
    public AppearanceSection()
    {
        InitializeComponent();
    }

    // Navigating to another section detaches this view; the live-text preview ends with it.
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        (DataContext as AppearanceSectionViewModel)?.StopLiveTextPreview();
    }
}
