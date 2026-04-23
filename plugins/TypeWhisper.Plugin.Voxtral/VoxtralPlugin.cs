using System.Net.Http;
using System.Net.Http.Headers;
using System.Windows;
using System.Windows.Controls;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Helpers;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.Plugin.Voxtral;

public sealed class VoxtralPlugin : ITranscriptionEnginePlugin
{
    private const string BaseUrl = "https://api.mistral.ai";
    private const string ModelId = "mistral-whisper";

    private readonly HttpClient _httpClient = new() { Timeout = TimeSpan.FromSeconds(60) };
    private IPluginHostServices? _host;
    private string? _apiKey;
    private string? _selectedModelId;

    // ITypeWhisperPlugin

    public string PluginId => "com.typewhisper.voxtral";
    public string PluginName => "Voxtral";
    public string PluginVersion => "1.0.0";

    public async Task ActivateAsync(IPluginHostServices host)
    {
        _host = host;
        _apiKey = await host.LoadSecretAsync("api-key");
        host.Log(PluginLogLevel.Info, $"Activated (configured={IsConfigured})");
    }

    public Task DeactivateAsync()
    {
        _host = null;
        return Task.CompletedTask;
    }

    public UserControl? CreateSettingsView()
    {
        var panel = new StackPanel { Margin = new Thickness(8) };

        var label = new TextBlock
        {
            Text = "Mistral API Key",
            Margin = new Thickness(0, 0, 0, 4),
        };

        var box = new PasswordBox { MaxLength = 200 };
        if (!string.IsNullOrEmpty(_apiKey))
            box.Password = _apiKey;

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
            var key = box.Password.Trim();
            _apiKey = string.IsNullOrWhiteSpace(key) ? null : key;
            if (_host is not null)
            {
                if (string.IsNullOrWhiteSpace(key))
                    await _host.DeleteSecretAsync("api-key");
                else
                    await _host.StoreSecretAsync("api-key", key);
            }
            status.Text = string.IsNullOrWhiteSpace(key) ? "" : "Saved";
            _host?.NotifyCapabilitiesChanged();
        };

        panel.Children.Add(label);
        panel.Children.Add(box);
        panel.Children.Add(btn);
        panel.Children.Add(status);
        return new UserControl { Content = panel };
    }

    // ITranscriptionEnginePlugin

    public string ProviderId => "voxtral";
    public string ProviderDisplayName => "Voxtral";
    public bool IsConfigured => !string.IsNullOrEmpty(_apiKey);

    public IReadOnlyList<PluginModelInfo> TranscriptionModels { get; } =
        [new PluginModelInfo(ModelId, "Voxtral (Mistral Whisper)")];

    public string? SelectedModelId => _selectedModelId;
    public bool SupportsTranslation => true;

    public void SelectModel(string modelId)
    {
        if (modelId != ModelId)
            throw new ArgumentException($"Unknown model: {modelId}");
        _selectedModelId = modelId;
    }

    public async Task<PluginTranscriptionResult> TranscribeAsync(
        byte[] wavAudio, string? language, bool translate, string? prompt, CancellationToken ct)
    {
        if (!IsConfigured)
            throw new InvalidOperationException("Plugin not configured. Mistral API key required.");

        return await OpenAiTranscriptionHelper.TranscribeAsync(
            _httpClient, BaseUrl, _apiKey!, ModelId,
            wavAudio, language, translate, "verbose_json", ct, prompt);
    }

    internal string? ApiKey => _apiKey;

    internal async Task<bool> ValidateApiKeyAsync(string apiKey, CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{BaseUrl}/v1/models");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        try
        {
            var response = await _httpClient.SendAsync(request, ct);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    public void Dispose() => _httpClient.Dispose();
}
