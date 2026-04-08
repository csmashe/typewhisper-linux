using System.Net.Http;
using System.Net.Http.Headers;
using System.Windows;
using System.Windows.Controls;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Helpers;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.Plugin.Qwen3Stt;

public sealed class Qwen3SttPlugin : ITranscriptionEnginePlugin
{
    private const string DefaultBaseUrl = "http://localhost:8000";
    private const string DefaultModel = "Qwen/Qwen3-ASR";

    private readonly HttpClient _httpClient = new() { Timeout = TimeSpan.FromSeconds(60) };
    private IPluginHostServices? _host;
    private string? _apiKey;
    private string? _baseUrl;
    private string? _selectedModelId;

    // ITypeWhisperPlugin

    public string PluginId => "com.typewhisper.qwen3-stt";
    public string PluginName => "Qwen3 STT";
    public string PluginVersion => "1.0.0";

    public async Task ActivateAsync(IPluginHostServices host)
    {
        _host = host;
        _apiKey = await host.LoadSecretAsync("api-key");
        _baseUrl = host.GetSetting<string>("baseUrl");
        if (string.IsNullOrWhiteSpace(_baseUrl))
            _baseUrl = DefaultBaseUrl;
        host.Log(PluginLogLevel.Info, $"Activated (baseUrl={_baseUrl}, configured={IsConfigured})");
    }

    public Task DeactivateAsync()
    {
        _host = null;
        return Task.CompletedTask;
    }

    public UserControl? CreateSettingsView()
    {
        var panel = new StackPanel { Margin = new Thickness(8) };

        // Base URL
        var urlLabel = new TextBlock
        {
            Text = "Base URL",
            Margin = new Thickness(0, 0, 0, 4),
        };
        var urlBox = new TextBox
        {
            Text = _baseUrl ?? DefaultBaseUrl,
            MaxLength = 500,
        };

        // API Key
        var keyLabel = new TextBlock
        {
            Text = "API Key (optional)",
            Margin = new Thickness(0, 12, 0, 4),
        };
        var keyBox = new PasswordBox { MaxLength = 200 };
        if (!string.IsNullOrEmpty(_apiKey))
            keyBox.Password = _apiKey;

        var status = new TextBlock
        {
            Margin = new Thickness(0, 4, 0, 0),
            FontSize = 12,
        };

        var btn = new Button
        {
            Content = "Save",
            Margin = new Thickness(0, 8, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        btn.Click += async (_, _) =>
        {
            // Save base URL
            var url = urlBox.Text.Trim().TrimEnd('/');
            if (url.EndsWith("/v1", StringComparison.OrdinalIgnoreCase))
                url = url[..^3];
            _baseUrl = string.IsNullOrWhiteSpace(url) ? DefaultBaseUrl : url;
            _host?.SetSetting("baseUrl", _baseUrl);

            // Save API key
            var key = keyBox.Password.Trim();
            _apiKey = string.IsNullOrWhiteSpace(key) ? null : key;
            if (_host is not null)
            {
                if (string.IsNullOrWhiteSpace(key))
                    await _host.DeleteSecretAsync("api-key");
                else
                    await _host.StoreSecretAsync("api-key", key);
            }

            status.Text = "Saved";
            _host?.NotifyCapabilitiesChanged();
        };

        panel.Children.Add(urlLabel);
        panel.Children.Add(urlBox);
        panel.Children.Add(keyLabel);
        panel.Children.Add(keyBox);
        panel.Children.Add(btn);
        panel.Children.Add(status);
        return new UserControl { Content = panel };
    }

    // ITranscriptionEnginePlugin

    public string ProviderId => "qwen3-stt";
    public string ProviderDisplayName => "Qwen3 STT";
    public bool IsConfigured => !string.IsNullOrEmpty(_baseUrl);

    public IReadOnlyList<PluginModelInfo> TranscriptionModels { get; } =
        [new PluginModelInfo("Qwen/Qwen3-ASR", "Qwen3 ASR")];

    public string? SelectedModelId => _selectedModelId;
    public bool SupportsTranslation => false;

    public void SelectModel(string modelId)
    {
        if (modelId != DefaultModel)
            throw new ArgumentException($"Unknown model: {modelId}");
        _selectedModelId = modelId;
    }

    public async Task<PluginTranscriptionResult> TranscribeAsync(
        byte[] wavAudio, string? language, bool translate, string? prompt, CancellationToken ct)
    {
        if (!IsConfigured)
            throw new InvalidOperationException("Plugin not configured. Base URL required.");

        var baseUrl = _baseUrl ?? DefaultBaseUrl;
        var apiKey = _apiKey ?? "";
        var model = _selectedModelId ?? DefaultModel;

        return await OpenAiTranscriptionHelper.TranscribeAsync(
            _httpClient, baseUrl, apiKey, model,
            wavAudio, language, translate: false, "verbose_json", ct);
    }

    public void Dispose() => _httpClient.Dispose();
}
