using TypeWhisper.Windows.ViewModels;
using TypeWhisper.Windows.Views.Sections;
using Wpf.Ui.Controls;

namespace TypeWhisper.Windows.Views;

public partial class SettingsWindow : FluentWindow
{
    public SettingsWindow(SettingsWindowViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;

        viewModel.RegisterSection("Dashboard", () => new DashboardSection { DataContext = viewModel });
        viewModel.RegisterSection("Allgemein", () => new GeneralSection { DataContext = viewModel });
        viewModel.RegisterSection("Aufnahme", () => new AudioSection { DataContext = viewModel });
        viewModel.RegisterSection("Modelle", () => new ModelsSection { DataContext = viewModel });
        viewModel.RegisterSection("Profile", () => new ProfilesSection { DataContext = viewModel });
        viewModel.RegisterSection("Wörterbuch", () => new DictionarySection { DataContext = viewModel });
        viewModel.RegisterSection("Snippets", () => new SnippetsSection { DataContext = viewModel });
        viewModel.RegisterSection("Prompts", () => new PromptsSection { DataContext = viewModel });
        viewModel.RegisterSection("Erweiterungen", () => new PluginsSection { DataContext = viewModel });
        viewModel.RegisterSection("Verlauf", () => new HistorySection { DataContext = viewModel });
        viewModel.RegisterSection("Info", () => new InfoSection { DataContext = viewModel });

        viewModel.NavigateToDefault();
    }
}
