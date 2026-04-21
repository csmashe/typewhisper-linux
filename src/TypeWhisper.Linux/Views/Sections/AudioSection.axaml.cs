using Avalonia.Controls;
using TypeWhisper.Linux.ViewModels.Sections;

namespace TypeWhisper.Linux.Views.Sections;

public partial class AudioSection : UserControl
{
    public AudioSection()
    {
        InitializeComponent();
        AttachedToVisualTree += (_, _) => (DataContext as AudioSectionViewModel)?.ActivatePreview();
        DetachedFromVisualTree += (_, _) => (DataContext as AudioSectionViewModel)?.DeactivatePreview();
    }
}
