using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Windows.Controls;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.Plugin.CloudflareAsr;

public sealed class CloudflareAsrPlugin : ITranscriptionEnginePlugin, TypeWhisper.PluginSDK.Wpf.IWpfPluginSettingsProvider
{
    private readonly HttpClient _httpClient = new() { Timeout = TimeSpan.FromSeconds(120) };
    private IPluginHostServices? _host;
    private string? _apiToken;
    private string? _accountId;
    private string? _selectedModelId;

    private static readonly IReadOnlyList<PluginModelInfo> Models =
    [
        new("whisper", "Whisper (Cloudflare)"),
    ];

    // ITypeWhisperPlugin

    public string PluginId => "com.typewhisper.cloudflare-asr";
    public string PluginName => "Cloudflare ASR";
    public string PluginVersion => "1.0.0";

    public async Task ActivateAsync(IPluginHostServices host)
    {
        _host = host;
        _apiToken = await host.LoadSecretAsync("api-token");
        _accountId = await host.LoadSecretAsync("account-id");
        host.Log(PluginLogLevel.Info, $"Activated (configured={IsConfigured})");
    }

    public Task DeactivateAsync()
    {
        _host = null;
        return Task.CompletedTask;
    }

    public UserControl? CreateSettingsView()
    {
        var panel = new StackPanel { Margin = new System.Windows.Thickness(8) };

        // Account ID field
        var accountLabel = new TextBlock { Text = "Account ID", Margin = new System.Windows.Thickness(0, 0, 0, 4) };
        var accountBox = new TextBox { MaxLength = 200 };
        if (!string.IsNullOrEmpty(_accountId)) accountBox.Text = _accountId;

        // API Token field
        var tokenLabel = new TextBlock { Text = "API Token", Margin = new System.Windows.Thickness(0, 12, 0, 4) };
        var tokenBox = new PasswordBox { MaxLength = 200 };
        if (!string.IsNullOrEmpty(_apiToken)) tokenBox.Password = _apiToken;

        var btn = new Button
        {
            Content = "Save",
            Margin = new System.Windows.Thickness(0, 12, 0, 0),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
        };
        btn.Click += async (_, _) =>
        {
            var account = accountBox.Text;
            var token = tokenBox.Password;

            _accountId = string.IsNullOrWhiteSpace(account) ? null : account.Trim();
            _apiToken = string.IsNullOrWhiteSpace(token) ? null : token;

            if (_host is not null)
            {
                if (string.IsNullOrWhiteSpace(account))
                    await _host.DeleteSecretAsync("account-id");
                else
                    await _host.StoreSecretAsync("account-id", account.Trim());

                if (string.IsNullOrWhiteSpace(token))
                    await _host.DeleteSecretAsync("api-token");
                else
                    await _host.StoreSecretAsync("api-token", token);
            }
        };

        panel.Children.Add(accountLabel);
        panel.Children.Add(accountBox);
        panel.Children.Add(tokenLabel);
        panel.Children.Add(tokenBox);
        panel.Children.Add(btn);
        return new UserControl { Content = panel };
    }

    // ITranscriptionEnginePlugin

    public string ProviderId => "cloudflare-asr";
    public string ProviderDisplayName => "Cloudflare ASR";
    public bool IsConfigured => !string.IsNullOrEmpty(_apiToken) && !string.IsNullOrEmpty(_accountId);

    public IReadOnlyList<PluginModelInfo> TranscriptionModels => Models;

    public string? SelectedModelId => _selectedModelId;

    public bool SupportsTranslation => false;

    public void SelectModel(string modelId)
    {
        if (Models.All(m => m.Id != modelId))
            throw new ArgumentException($"Unknown model: {modelId}");
        _selectedModelId = modelId;
    }

    public async Task<PluginTranscriptionResult> TranscribeAsync(
        byte[] wavAudio, string? language, bool translate, string? prompt, CancellationToken ct)
    {
        if (!IsConfigured)
            throw new InvalidOperationException("Plugin not configured. Account ID and API token required.");

        var url = $"https://api.cloudflare.com/client/v4/accounts/{_accountId}/ai/run/@cf/openai/whisper";

        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiToken);
        request.Content = new ByteArrayContent(wavAudio);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");

        var response = await _httpClient.SendAsync(request, ct);
        var json = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"Cloudflare API error {(int)response.StatusCode}: {json}");

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var text = "";
        if (root.TryGetProperty("result", out var result))
        {
            if (result.TryGetProperty("text", out var textEl))
                text = textEl.GetString() ?? "";
        }

        // Cloudflare Workers AI Whisper doesn't return duration or language detection
        // in the standard response, so we use defaults
        string? detectedLanguage = null;
        if (root.TryGetProperty("result", out var res)
            && res.TryGetProperty("language", out var langEl))
        {
            detectedLanguage = langEl.GetString();
        }

        double duration = 0;
        if (root.TryGetProperty("result", out var res2)
            && res2.TryGetProperty("duration", out var durEl))
        {
            duration = durEl.GetDouble();
        }

        return new PluginTranscriptionResult(text.Trim(), detectedLanguage, duration, NoSpeechProbability: null);
    }

    public void Dispose()
    {
        _httpClient.Dispose();
    }
}
