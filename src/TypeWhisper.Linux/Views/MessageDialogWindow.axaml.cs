using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;

namespace TypeWhisper.Linux.Views;

public partial class MessageDialogWindow : Window
{
    private bool _result;

    public MessageDialogWindow()
    {
        InitializeComponent();
    }

    public async Task ShowMessageAsync(string title, string message)
    {
        ConfigureButtons(isConfirmation: false);
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

    public async Task<bool> ShowConfirmationAsync(string title, string message, string confirmText = "OK", string cancelText = "Cancel")
    {
        ConfigureButtons(isConfirmation: true, confirmText, cancelText);
        _result = false;
        Title = title;
        TitleTextBlock.Text = title;
        MessageTextBlock.Text = message;

        if (Avalonia.Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop
            && desktop.MainWindow is { } owner)
        {
            var result = await ShowDialog<bool>(owner);
            return result;
        }

        Show();
        return false;
    }

    private void OkButton_OnClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        _result = true;
        Close(_result);
    }

    private void CancelButton_OnClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        _result = false;
        Close(_result);
    }

    private void ConfigureButtons(bool isConfirmation, string confirmText = "OK", string cancelText = "Cancel")
    {
        OkButton.Content = confirmText;
        CancelButton.Content = cancelText;
        CancelButton.IsVisible = isConfirmation;
    }
}
