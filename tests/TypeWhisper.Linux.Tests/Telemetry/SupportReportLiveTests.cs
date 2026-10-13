using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TypeWhisper.Core;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Linux.Services;
using TypeWhisper.Linux.Services.Plugins;
using TypeWhisper.Linux.Services.Telemetry;
using Xunit;

namespace TypeWhisper.Linux.Tests.Telemetry;

/// <summary>
///     Builds a report from the app's real services and data. Run it against a copy of a real data folder
///     (XDG_DATA_HOME pointing at the copy) with TYPEWHISPER_SUPPORT_REPORT_OUT naming the report to write.
/// </summary>
public sealed class SupportReportLiveTests
{
    private static string? Output => Environment.GetEnvironmentVariable("TYPEWHISPER_SUPPORT_REPORT_OUT");

    [SupportReportLiveFact]
    public async Task Real_data_folder_produces_a_content_free_report()
    {
        var services = new ServiceCollection();
        ServiceRegistrations.Register(services);
        await using var provider = services.BuildServiceProvider();
        await provider.GetRequiredService<PluginManager>().InitializeAsync();

        var report = await provider.GetRequiredService<SupportReportService>().CaptureAsync();
        report.Export(Output!);

        var json = await File.ReadAllTextAsync(Output!);
        using var document = JsonDocument.Parse(json);
        Assert.Equal("linux", document.RootElement.GetProperty("platform").GetString());
        var forbidden = new List<string> { TypeWhisperEnvironment.BasePath, Environment.UserName };
        forbidden.AddRange(AudioRecordingService.GetInputDevices().Select(device => device.Name));
        forbidden.AddRange(provider.GetRequiredService<IProfileService>().Profiles.Select(profile => profile.Name));
        forbidden.AddRange(provider.GetRequiredService<IPromptActionService>().Actions.Select(prompt => prompt.SystemPrompt));
        forbidden.AddRange(provider.GetRequiredService<IDictionaryService>().Entries.Select(entry => entry.Original));
        forbidden.AddRange(provider.GetRequiredService<ISnippetService>().Snippets.Select(snippet => snippet.Trigger));
        var values = StringValues(document.RootElement).ToArray();
        // A short name such as "Email" can be part of a schema value like "FormalEmail", so only longer
        // values are searched inside reported values.
        foreach (var value in forbidden.Where(value => value.Length >= 4).Distinct())
        {
            Assert.DoesNotContain(values, reported => value.Length >= 12
                ? reported.Contains(value, StringComparison.OrdinalIgnoreCase)
                : reported.Equals(value, StringComparison.OrdinalIgnoreCase));
        }
    }

    // Property names are fixed by the schema; only values can carry user data.
    private static IEnumerable<string> StringValues(JsonElement element) =>
        element.ValueKind switch
        {
            JsonValueKind.String => [element.GetString()!],
            JsonValueKind.Object => element.EnumerateObject().SelectMany(property => StringValues(property.Value)),
            JsonValueKind.Array => element.EnumerateArray().SelectMany(StringValues),
            _ => [],
        };

    private sealed class SupportReportLiveFactAttribute : FactAttribute
    {
        public SupportReportLiveFactAttribute()
        {
            if (string.IsNullOrWhiteSpace(Output))
            {
                Skip = "Set TYPEWHISPER_SUPPORT_REPORT_OUT, with XDG_DATA_HOME pointing at a copied data folder.";
            }
        }
    }
}
