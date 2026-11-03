using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.TranscriptionWorkerProbe;

/// <summary>
///     A local engine for worker tests. The language selects a misbehaviour; otherwise the result
///     text reports what the engine saw as <c>key=value</c> pairs separated by semicolons.
/// </summary>
public sealed partial class WorkerProbePlugin : ITranscriptionEnginePlugin
{
    public const string Id = "test.typewhisper.worker-probe";
    private const string SettingKey = "probe-setting";
    private const string FailActivationKey = "probe-fail-activation";

    private const int SigStop = 19;

    private IPluginHostServices? _host;
    private string? _loadedModelId;
    private string? _computeBackend;

    public string PluginId => Id;
    public string PluginName => "Worker probe";
    public string PluginVersion => "1.0.0";
    public string ProviderId => "worker-probe";
    public string ProviderDisplayName => "Worker probe";
    public bool IsConfigured => true;

    public IReadOnlyList<PluginModelInfo> TranscriptionModels { get; } =
        [new("small", "Small"), new("large", "Large"), new("broken", "Broken")];

    public string? SelectedModelId { get; private set; }
    public bool SupportsTranslation => true;

    public IReadOnlyList<TranscriptionAccelerationBackend> SupportedAccelerationBackends { get; } =
        [TranscriptionAccelerationBackend.Cpu, TranscriptionAccelerationBackend.NvidiaCuda];

    public TranscriptionAccelerationPreference AccelerationPreference { get; private set; } =
        TranscriptionAccelerationPreference.Auto;

    public TranscriptionAccelerationStatus AccelerationStatus =>
        new(
            AccelerationPreference == TranscriptionAccelerationPreference.NvidiaCuda
                ? TranscriptionAccelerationBackend.NvidiaCuda
                : TranscriptionAccelerationBackend.Cpu,
            $"Probe {AccelerationPreference} in {Environment.ProcessId}"
        );

    public Task ActivateAsync(IPluginHostServices host)
    {
        _host = host;
        switch (host.GetSetting<string>(FailActivationKey))
        {
            case "refuse":
                throw new InvalidOperationException("The probe refused to activate.");
            case "missing":
                throw new FileNotFoundException("The probe runtime is missing.");
        }

        host.Log(PluginLogLevel.Info, $"activated in {Environment.ProcessId}");
        return Task.CompletedTask;
    }

    public Task DeactivateAsync()
    {
        return Task.CompletedTask;
    }

    public void SelectModel(string modelId)
    {
        SelectedModelId = modelId;
    }

    public void SetAccelerationPreference(TranscriptionAccelerationPreference preference)
    {
        AccelerationPreference = preference;
        _computeBackend = null;
    }

    public Task ConfigureComputeBackendAsync(string backend)
    {
        _computeBackend = backend;
        return Task.CompletedTask;
    }

    public Task LoadModelAsync(string modelId, CancellationToken ct)
    {
        if (modelId == "broken")
        {
            throw new FileNotFoundException("The probe model is missing.");
        }

        _loadedModelId = modelId;
        _host?.Log(PluginLogLevel.Info, $"loaded {modelId} in {Environment.ProcessId}");
        return Task.CompletedTask;
    }

    public async Task<PluginTranscriptionResult> TranscribeAsync(
        byte[] wavAudio,
        string? language,
        bool translate,
        string? prompt,
        CancellationToken ct
    )
    {
        switch (language)
        {
            case "crash":
                // A real fault inside native code, which the runtime cannot turn into an exception.
                _ = memset(8, 0, 1);
                break;
            case "crash-once":
                // Faults in the first worker only; the marker outlives it.
                var marker = Path.Join(_host!.PluginDataDirectory, "crashed-once");
                if (!File.Exists(marker))
                {
                    Directory.CreateDirectory(_host.PluginDataDirectory);
                    await File.WriteAllTextAsync(marker, "", ct);
                    _ = memset(8, 0, 1);
                }

                break;
            case "crash-on-gpu":
                // Like a graphics driver fault: the CPU path works.
                if (AccelerationPreference != TranscriptionAccelerationPreference.Cpu)
                {
                    _ = memset(8, 0, 1);
                }

                break;
            case "freeze":
                // Stops every thread, the heartbeat included.
                _ = kill(Environment.ProcessId, SigStop);
                break;
            case "hang":
                // Blocks like a stuck native call: the request ignores cancellation, the heartbeat runs.
                Thread.Sleep(Timeout.Infinite);
                break;
            case "slow":
                await Task.Delay(Timeout.Infinite, ct);
                break;
            case "fail":
                throw new NotSupportedException("The probe refused this audio.");
            case "spawn":
                Spawn();
                break;
            case "spawn-and-hang":
                Spawn();
                Thread.Sleep(Timeout.Infinite);
                break;
        }

        var setting = _host?.GetSetting<string>(SettingKey);
        // Settings written in the worker must never reach the host's settings file.
        _host?.SetSetting(SettingKey, "written-by-worker");
        return new PluginTranscriptionResult(
            string.Join(
                ';',
                $"pid={Environment.ProcessId}",
                $"model={_loadedModelId}",
                $"selected={SelectedModelId}",
                $"length={wavAudio.Length.ToString(CultureInfo.InvariantCulture)}",
                $"language={language}",
                $"translate={translate}",
                $"prompt={prompt}",
                $"preference={AccelerationPreference}",
                $"setting={setting}",
                $"backend={_computeBackend}"
            ),
            language,
            1.5,
            0.25f
        )
        {
            Segments = [new PluginTranscriptionSegment("probe", 0, 1) { NoSpeechProbability = 0.25f }],
            TokenTimings = [new VocabularyTokenTiming("probe", 0.25, 0.5)],
        };
    }

    private void Spawn()
    {
        using var child = Process.Start(new ProcessStartInfo("sleep", "300") { UseShellExecute = false });
        _host?.Log(PluginLogLevel.Info, $"spawned {child!.Id}");
    }

    public Task<PluginTranscriptionResult> TranscribeWithLanguageHintsAsync(
        byte[] wavAudio,
        IReadOnlyList<string> languageHints,
        bool translate,
        string? prompt,
        CancellationToken ct
    )
    {
        return TranscribeAsync(wavAudio, "hints:" + string.Join(',', languageHints), translate, prompt, ct);
    }

    public void Dispose()
    {
    }

    // ReSharper disable once InconsistentNaming -- native libc function name.
    [LibraryImport("libc")]
    private static partial int kill(int pid, int signal);

    // ReSharper disable once InconsistentNaming -- native libc function name.
    [LibraryImport("libc")]
    private static partial nint memset(nint destination, int value, nuint count);
}
