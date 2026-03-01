using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace TypeWhisper.Plugin.OpenAiCompatible;

public partial class OpenAiCompatibleSettingsView : UserControl
{
    private readonly OpenAiCompatiblePlugin _plugin;

    public OpenAiCompatibleSettingsView(OpenAiCompatiblePlugin plugin)
    {
        _plugin = plugin;
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        UrlBox.Text = _plugin.BaseUrl ?? "http://localhost:11434";

        if (!string.IsNullOrEmpty(_plugin.ApiKey))
            ApiKeyBox.Password = _plugin.ApiKey;

        ManualTranscriptionBox.Text = _plugin.SelectedTranscriptionModelId ?? "";
        ManualLlmBox.Text = _plugin.SelectedLlmModelId ?? "";

        if (_plugin.IsConfigured)
        {
            ModelsSection.Visibility = Visibility.Visible;
            var models = _plugin.FetchedModels.ToList();
            PopulateModels(models);

            if (models.Count > 0)
                ShowConnectionSuccess(models.Count, _plugin.BaseUrl ?? "");
        }
    }

    private async void OnConnectClick(object sender, RoutedEventArgs e)
    {
        e.Handled = true;

        var url = UrlBox.Text.Trim();
        if (string.IsNullOrEmpty(url))
            return;

        _plugin.SetBaseUrl(url);

        var key = ApiKeyBox.Password.Trim();
        if (!string.IsNullOrEmpty(key))
            await _plugin.SetApiKeyAsync(key);

        ConnectButton.IsEnabled = false;
        ConnectButton.Content = "Verbinde...";
        ShowConnectionStatus("\u23F3", "Verbinde...", $"Versuche Verbindung zu {url}...", Brushes.Gray);

        try
        {
            using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(10));
            var models = await _plugin.FetchModelsAsync(cts.Token);
            var connected = models.Count > 0;

            if (!connected)
            {
                using var cts2 = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(10));
                connected = await _plugin.ValidateConnectionAsync(cts2.Token);
            }

            if (connected)
            {
                _plugin.SetFetchedModels(models);
                ModelsSection.Visibility = Visibility.Visible;
                PopulateModels(models);
                ShowConnectionSuccess(models.Count, url);
            }
            else
            {
                ShowConnectionStatus("\u274C", "Verbindung fehlgeschlagen",
                    $"Server {url} antwortet nicht oder der /v1/models Endpunkt ist nicht verfügbar.",
                    Brushes.Red);
            }
        }
        catch (OperationCanceledException)
        {
            ShowConnectionStatus("\u274C", "Zeitüberschreitung",
                $"Server {url} hat nicht innerhalb von 10 Sekunden geantwortet.",
                Brushes.Red);
        }
        catch (Exception ex)
        {
            var detail = ex.Message;
            if (ex.InnerException is not null)
                detail += $" ({ex.InnerException.Message})";

            ShowConnectionStatus("\u274C", "Verbindungsfehler", detail, Brushes.Red);
        }
        finally
        {
            ConnectButton.IsEnabled = true;
            ConnectButton.Content = "Verbinden";
        }
    }

    private void ShowConnectionSuccess(int modelCount, string url)
    {
        var transcriptionCount = 0;
        var llmCount = 0;

        // All models could be used for either purpose
        transcriptionCount = modelCount;
        llmCount = modelCount;

        var detail = $"Server: {url}\n{modelCount} Modell(e) verfügbar";
        ShowConnectionStatus("\u2705", $"Verbunden — {modelCount} Modelle", detail,
            new SolidColorBrush(Color.FromRgb(0x4C, 0xAF, 0x50)));
    }

    private void ShowConnectionStatus(string icon, string status, string detail, Brush color)
    {
        ConnectionStatusPanel.Visibility = Visibility.Visible;
        ConnectionStatusIcon.Text = icon;
        ConnectionStatus.Text = status;
        ConnectionStatus.Foreground = color;
        ConnectionDetail.Text = detail;
    }

    private void OnApiKeyChanged(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        _ = _plugin.SetApiKeyAsync(ApiKeyBox.Password);
    }

    private async void OnRefreshClick(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        RefreshButton.IsEnabled = false;
        try
        {
            var models = await _plugin.FetchModelsAsync();
            _plugin.SetFetchedModels(models);
            PopulateModels(models);

            if (models.Count > 0)
                ShowConnectionSuccess(models.Count, _plugin.BaseUrl ?? "");
        }
        finally
        {
            RefreshButton.IsEnabled = true;
        }
    }

    private void PopulateModels(List<FetchedModel> models)
    {
        if (models.Count > 0)
        {
            PickerSection.Visibility = Visibility.Visible;
            ManualSection.Visibility = Visibility.Collapsed;

            TranscriptionModelPicker.ItemsSource = models;
            LlmModelPicker.ItemsSource = models;

            var selectedTranscription = models.FirstOrDefault(m => m.Id == _plugin.SelectedTranscriptionModelId);
            TranscriptionModelPicker.SelectedItem = selectedTranscription ?? models.FirstOrDefault();

            var selectedLlm = models.FirstOrDefault(m => m.Id == _plugin.SelectedLlmModelId);
            LlmModelPicker.SelectedItem = selectedLlm ?? models.FirstOrDefault();
        }
        else
        {
            PickerSection.Visibility = Visibility.Collapsed;
            ManualSection.Visibility = Visibility.Visible;
        }
    }

    private void OnTranscriptionModelChanged(object sender, SelectionChangedEventArgs e)
    {
        e.Handled = true;
        if (TranscriptionModelPicker.SelectedItem is FetchedModel model)
            _plugin.SelectModel(model.Id);
    }

    private void OnLlmModelChanged(object sender, SelectionChangedEventArgs e)
    {
        e.Handled = true;
        if (LlmModelPicker.SelectedItem is FetchedModel model)
            _plugin.SelectLlmModel(model.Id);
    }

    private void OnSaveManualTranscription(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        var id = ManualTranscriptionBox.Text.Trim();
        if (!string.IsNullOrEmpty(id))
            _plugin.SelectModel(id);
    }

    private void OnSaveManualLlm(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        var id = ManualLlmBox.Text.Trim();
        if (!string.IsNullOrEmpty(id))
            _plugin.SelectLlmModel(id);
    }
}
