using System;
using Avalonia.Controls;
using TypeWhisper.Linux.ViewModels.Sections;

namespace TypeWhisper.Linux.Views.Sections;

public partial class PluginsSection : UserControl
{
    public PluginsSection()
    {
        InitializeComponent();
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
