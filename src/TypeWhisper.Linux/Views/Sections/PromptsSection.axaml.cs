using Avalonia.Controls;
using TypeWhisper.Linux.ViewModels.Sections;

namespace TypeWhisper.Linux.Views.Sections;

public partial class PromptsSection : UserControl
{
    public PromptsSection()
    {
        InitializeComponent();
    }

    // Re-poll the configured LLM provider(s) for their current model list every
    // time the provider/model dropdown opens, so newly added server-side models
    // (e.g. a freshly pulled Ollama model) appear without a manual "Validate".
    private void OnProviderDropDownOpened(object? sender, EventArgs e)
    {
        if (DataContext is PromptsSectionViewModel vm)
        {
            _ = vm.RefreshProviderModelsAsync();
        }
    }
}
