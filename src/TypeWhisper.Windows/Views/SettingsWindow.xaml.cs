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

        viewModel.RegisterSection("Home", () => new DashboardSection { DataContext = viewModel });
        viewModel.RegisterSection("General", () => new GeneralSection { DataContext = viewModel });
        viewModel.RegisterSection("Recording", () => new AudioSection { DataContext = viewModel });
        viewModel.RegisterSection("Models", () => new ModelsSection { DataContext = viewModel });
        viewModel.RegisterSection("History", () => new HistorySection { DataContext = viewModel });
        viewModel.RegisterSection("Dictionary", () => new DictionarySection { DataContext = viewModel });
        viewModel.RegisterSection("Snippets", () => new SnippetsSection { DataContext = viewModel });
        viewModel.RegisterSection("Profiles", () => new ProfilesSection { DataContext = viewModel });
        viewModel.RegisterSection("Prompts", () => new PromptsSection { DataContext = viewModel });
        viewModel.RegisterSection("Recorder", () => new RecorderSection { DataContext = viewModel });
        viewModel.RegisterSection("Plugins", () => new PluginsSection { DataContext = viewModel });
        viewModel.RegisterSection("Advanced", () => new AdvancedSection { DataContext = viewModel });
        viewModel.RegisterSection("License", () => new LicenseSection { DataContext = viewModel });
        viewModel.RegisterSection("About", () => new InfoSection { DataContext = viewModel });

        viewModel.NavigateToDefault();

        Closing += (_, _) => viewModel.Settings.StopMicrophonePreview();
    }
}
