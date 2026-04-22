using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;

namespace TypeWhisper.Linux.Views;

public partial class MessageDialogWindow : Window
{
    public MessageDialogWindow()
    {
        InitializeComponent();
    }

    public async Task ShowMessageAsync(string title, string message)
    {
        Title = title;
        TitleTextBlock.Text = title;
        MessageTextBlock.Text = message;

        if (Avalonia.Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop
            && desktop.MainWindow is { } owner)
        {
            await ShowDialog(owner);
        }
        else
        {
            Show();
        }
    }

    private void OkButton_OnClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        Close();
    }
}
