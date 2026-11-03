using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using TypeWhisper.Linux.ViewModels.Sections;

namespace TypeWhisper.Linux.Views.Sections;

public partial class PluginsSection : UserControl
{
    private PluginsSectionViewModel? _viewModel;

    public PluginsSection()
    {
        InitializeComponent();
        // A reveal that switched to this section fired before the view existed.
        Loaded += (_, _) => ScrollToRevealedPlugin();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_viewModel is not null)
        {
            _viewModel.PluginRevealed -= ScrollToRevealedPlugin;
        }

        _viewModel = DataContext as PluginsSectionViewModel;
        if (_viewModel is not null)
        {
            _viewModel.PluginRevealed += ScrollToRevealedPlugin;
        }
    }

    // The tab switch and expansion re-template the list first, so wait for layout.
    private void ScrollToRevealedPlugin()
    {
        Dispatcher.UIThread.Post(
            () =>
            {
                if (!IsLoaded || _viewModel?.TakeRevealedPlugin() is not { } row)
                {
                    return;
                }

                this.GetVisualDescendants()
                    .OfType<Border>()
                    .FirstOrDefault(border =>
                        border is { DataContext: PluginRow candidate, IsEffectivelyVisible: true }
                        && candidate.Id == row.Id
                    )
                    ?.BringIntoView();
            },
            DispatcherPriority.Loaded
        );
    }

    // Re-poll providers for current models when a *model* setting dropdown opens
    // (e.g. the OpenAI-compatible plugin's model field), so newly added models
    // appear without clicking "Validate". Other setting dropdowns (enums,
    // toggles) are skipped so they don't trigger needless network calls.
    private void OnSettingDropDownOpened(object? sender, EventArgs e)
    {
        if (sender is ComboBox { DataContext: PluginSettingFieldRow field }
            && field.Key.Contains("model", StringComparison.OrdinalIgnoreCase)
            && DataContext is PluginsSectionViewModel viewModel)
        {
            _ = viewModel.RefreshProviderModelsAsync();
        }
    }
}
