using Avalonia.Controls;
using TypeWhisper.Linux.ViewModels.Sections;

namespace TypeWhisper.Linux.Views.Sections;

public partial class DictationSection : UserControl
{
    public DictationSection()
    {
        InitializeComponent();
        AttachedToVisualTree += (_, _) => (DataContext as DictationSectionViewModel)?.ActivatePreview();
        DetachedFromVisualTree += (_, _) => (DataContext as DictationSectionViewModel)?.DeactivatePreview();
    }
}
