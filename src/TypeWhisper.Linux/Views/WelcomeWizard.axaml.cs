using Avalonia.Controls;
using TypeWhisper.Linux.ViewModels;

namespace TypeWhisper.Linux.Views;

public partial class WelcomeWizard : Window
{
    public WelcomeWizard() => InitializeComponent();

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (DataContext is WelcomeWizardViewModel vm)
            vm.RequestClose += (_, _) => Close();
    }
}
