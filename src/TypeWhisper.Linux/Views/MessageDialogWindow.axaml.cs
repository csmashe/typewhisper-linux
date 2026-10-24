using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Interactivity;
using TypeWhisper.Linux.Services.Localization;

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
        ConfigureButtons(false);
        Title = title;
        TitleTextBlock.Text = title;
        MessageTextBlock.Text = message;

        if (
            Application.Current?.ApplicationLifetime
            is IClassicDesktopStyleApplicationLifetime { MainWindow: { } owner })
        {
            await ShowDialog(owner);
        }
        else
        {
            Show();
        }
    }

    /// <summary>
    ///     Shows a confirm/cancel dialog and returns whether the user confirmed; false without
    ///     waiting when there is no main window to own it.
    /// </summary>
    /// <param name="title">The dialog title.</param>
    /// <param name="message">The question to confirm.</param>
    /// <param name="confirmText">The confirm button's text; OK when null.</param>
    /// <param name="cancelText">The cancel button's text; Cancel when null.</param>
    /// <param name="acknowledgement">
    ///     When set, a checkbox with this text that must be checked before the confirm button is
    ///     enabled; Cancel then has the focus.
    /// </param>
    public async Task<bool> ShowConfirmationAsync(
        string title,
        string message,
        string? confirmText = null,
        string? cancelText = null,
        string? acknowledgement = null
    )
    {
        confirmText ??= Loc.Instance["Common.OK"];
        cancelText ??= Loc.Instance["Common.Cancel"];
        ConfigureButtons(true, confirmText, cancelText);
        if (acknowledgement is not null)
        {
            AcknowledgementCheckBox.Content = acknowledgement;
            AcknowledgementCheckBox.IsChecked = false;
            AcknowledgementCheckBox.IsVisible = true;
            OkButton.IsEnabled = false;
            Opened += (_, _) => CancelButton.Focus();
        }

        _result = false;
        Title = title;
        TitleTextBlock.Text = title;
        MessageTextBlock.Text = message;

        if (
            Application.Current?.ApplicationLifetime
            is IClassicDesktopStyleApplicationLifetime { MainWindow: { } owner })
        {
            var result = await ShowDialog<bool>(owner);
            return result;
        }

        // No owning window available (e.g. called during startup). Fall back to
        // a non-modal Show(); the dialog will be independent and we return false
        // since we can't await a result.
        Show();
        return false;
    }

    private void OkButton_OnClick(object? sender, RoutedEventArgs e)
    {
        _result = true;
        Close(_result);
    }

    private void AcknowledgementCheckBox_OnIsCheckedChanged(object? sender, RoutedEventArgs e) =>
        OkButton.IsEnabled = AcknowledgementCheckBox.IsChecked == true;

    private void CancelButton_OnClick(object? sender, RoutedEventArgs e)
    {
        _result = false;
        Close(_result);
    }

    private void ConfigureButtons(
        bool isConfirmation,
        string? confirmText = null,
        string? cancelText = null
    )
    {
        OkButton.Content = confirmText ?? Loc.Instance["Common.OK"];
        CancelButton.Content = cancelText ?? Loc.Instance["Common.Cancel"];
        CancelButton.IsVisible = isConfirmation;
    }
}