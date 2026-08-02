using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text.Json;
using Avalonia.Controls;
using Microsoft.Extensions.DependencyInjection;
using TypeWhisper.Core;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;
using TypeWhisper.Core.Services;
using TypeWhisper.Integration.Tests.TestDoubles;
using TypeWhisper.Linux;
using TypeWhisper.Linux.Services;
using TypeWhisper.Linux.Services.ActiveWindow;
using TypeWhisper.Linux.Services.Hotkey.DeSetup;
using TypeWhisper.Linux.Services.Ipc;
using TypeWhisper.Linux.Services.Setup;
using TypeWhisper.Linux.ViewModels;
using TypeWhisper.Linux.ViewModels.Sections;
using Xunit;

namespace TypeWhisper.Integration.Tests;

public sealed class ServiceGraphLifecycleTests
{
    private const string ApiToken =
        "0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF";

    [Fact]
    [Trait("Category", "Integration")]
    public Task RealGraph_BootstrapsTransportsWatcherAndTearsDownCleanly()
    {
        return BoundedTest.RunAsync(async () =>
        {
            IntegrationEnvironment.ResetApplicationState();
            TypeWhisperEnvironment.EnsureDirectories();

            var port = GetFreeTcpPort();
            var services = new ServiceCollection();
            ServiceRegistrations.Register(services);
            var settings = (SettingsService)(services
                .Single(descriptor => descriptor.ServiceType == typeof(ISettingsService))
                .ImplementationInstance
                ?? throw new InvalidOperationException("The production settings registration changed."));
            settings.Save(
                AppSettings.Default with
                {
                    ApiServerEnabled = true,
                    ApiServerPort = port,
                    ApiServerBearerToken = ApiToken,
                    SelectedMicrophoneDevice = 0,
                    SoundFeedbackEnabled = false,
                    SpokenFeedbackEnabled = false,
                    LiveTranscriptionEnabled = false,
                    WatchFolderAutoStart = false,
                    TargetAppCorrectionLearningEnabled = false,
                    MemoryEnabled = false,
                }
            );

            var processRunner = new HeadlessProcessRunner();
            var systemAudio = new RecordingSystemAudio();
            var audioBoundary = new RecordingAudioBoundary();
            var playbackBoundary = new RecordingPlaybackBoundary();
            var deviceWatcher = new HeadlessDefaultDeviceWatcher();
            var sessionActivity = new HeadlessSessionActivityMonitor();
            var atSpi = new HeadlessAtSpiClient();
            var insertionPlatform = new RecordingTextInsertionPlatform();
            OrchestratorCompositionFixture.ReplaceExternalBoundaries(
                services,
                processRunner,
                systemAudio,
                audioBoundary,
                playbackBoundary,
                deviceWatcher,
                sessionActivity,
                atSpi,
                insertionPlatform
            );

            var provider = services.BuildServiceProvider(
                new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true }
            );
            var providerDisposed = false;
            try
            {
                var manifest = ValidateServiceManifest(services, provider);
                Assert.All(
                    [
                        typeof(LearnedCorrectionsToastController),
                        typeof(DictationSectionViewModel),
                        typeof(WelcomeWizardViewModel),
                        typeof(MainWindowViewModel),
                    ],
                    serviceType => AssertFenced(manifest, serviceType)
                );
                var registeredWindows = services
                    .Where(descriptor => typeof(Window).IsAssignableFrom(descriptor.ServiceType))
                    .Select(descriptor => descriptor.ServiceType)
                    .Distinct()
                    .ToArray();
                Assert.NotEmpty(registeredWindows);
                Assert.All(registeredWindows, serviceType => AssertFenced(manifest, serviceType));

                // ActiveWindowService and capability services perform construction-time
                // availability probes through the fail-closed process seam. The lifecycle
                // assertion below starts after construction/manifest validation.
                processRunner.Reset();

                var stages = App.CreateBootstrapStages(provider);
                var bootstrap = new App.BootstrapRunner(
                    stages,
                    provider.GetRequiredService<IErrorLogService>()
                );
                var report = await BoundedTest.WaitAsync(bootstrap.RunAsync());
                Assert.All(
                    report.Outcomes,
                    outcome => Assert.Equal(App.BootstrapStageStatus.Succeeded, outcome.Status)
                );

                var orchestrator = provider.GetRequiredService<DictationOrchestrator>();
                orchestrator.Initialize();

                var control = provider.GetRequiredService<ControlSocketServer>();
                await BoundedTest.WaitAsync(Task.Run(control.Start));
                var controlPath = SocketPathResolver.ResolveControlSocketPath();
                Assert.True(File.Exists(controlPath));
                var controlReply = await BoundedTest.WaitAsync(Task.Run(() =>
                {
                    var exchanged = ControlSocketClient.TrySendJson(
                        controlPath,
                        new JsonControlProtocol.Request
                        {
                            Version = JsonControlProtocol.CurrentVersion,
                            Command = JsonControlProtocol.CmdStatus,
                        },
                        out var response,
                        out var error
                    );
                    return (exchanged, response, error);
                }));
                Assert.True(controlReply.exchanged, controlReply.error);
                using (var controlJson = JsonDocument.Parse(controlReply.response))
                {
                    Assert.True(controlJson.RootElement.GetProperty("ok").GetBoolean());
                    Assert.Equal(
                        JsonControlProtocol.StateIdle,
                        controlJson.RootElement.GetProperty("state").GetString()
                    );
                }

                var watchPath = Path.Join(
                    IntegrationEnvironment.Root,
                    $"lifecycle-watch-{Guid.NewGuid():N}"
                );
                var outputPath = Path.Join(
                    IntegrationEnvironment.Root,
                    $"lifecycle-output-{Guid.NewGuid():N}"
                );
                Directory.CreateDirectory(watchPath);
                var watch = provider.GetRequiredService<WatchFolderService>();
                var watchHandlerCalled = false;
                await BoundedTest.WaitAsync(Task.Run(() =>
                    watch.Start(
                        new WatchFolderOptions(
                            watchPath,
                            outputPath,
                            WatchFolderOutputFormat.PlainText,
                            DeleteSource: false
                        ),
                        (_, _) =>
                        {
                            watchHandlerCalled = true;
                            return Task.FromException<WatchFolderTranscriptionResult>(
                                new InvalidOperationException(
                                    "The empty lifecycle watcher unexpectedly requested transcription."
                                )
                            );
                        }
                    )
                ));
                Assert.True(watch.IsRunning);

                var api = provider.GetRequiredService<HttpApiService>();
                await BoundedTest.WaitAsync(Task.Run(api.ApplySettings));
                Assert.StartsWith("Local API is running", api.StatusText, StringComparison.Ordinal);
                using (var client = new HttpClient())
                {
                    client.BaseAddress = new Uri($"http://127.0.0.1:{port}");
                    client.Timeout = TimeSpan.FromSeconds(3);
                    client.DefaultRequestHeaders.Authorization =
                        new AuthenticationHeaderValue("Bearer", ApiToken);
                    using var response = await BoundedTest.WaitAsync(
                        client.GetAsync("/v1/status")
                    );
                    Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                    using var body = JsonDocument.Parse(
                        await BoundedTest.WaitAsync(response.Content.ReadAsStringAsync())
                    );
                    Assert.Equal("1.0", body.RootElement.GetProperty("api_version").GetString());
                }

                var apiSocketPath = SocketPathResolver.ResolveApiSocketPath();
                var discoveryPath = Path.Join(
                    IntegrationEnvironment.ConfigHome,
                    "typewhisper",
                    "api-discovery.json"
                );
                Assert.True(File.Exists(apiSocketPath));
                Assert.True(File.Exists(discoveryPath));

                await BoundedTest.WaitAsync(
                    Task.Run(() => PrivateAppLifecycleInvoker.TearDownAsync(provider))
                );

                // Asserted before the container is disposed, because ControlSocketServer
                // and HttpApiService are container-created singletons: after disposal the
                // three file checks below pass even if TearDownAsync never runs, which is
                // exactly the shutdown ordering TearDownAsync exists to guarantee.
                Assert.False(File.Exists(controlPath));
                Assert.False(File.Exists(apiSocketPath));
                Assert.False(File.Exists(discoveryPath));
                Assert.True(deviceWatcher.Disposed);
                Assert.True(playbackBoundary.TerminateCount > 0);
                Assert.Equal(0, audioBoundary.ActiveStreams);

                await BoundedTest.WaitAsync(
                    Task.Run(async () => await provider.DisposeAsync())
                );
                providerDisposed = true;

                // Detects DI drift, production-stage failures, real server protocol or
                // bind failures, and watcher lifecycle leaks. WatchFolderService is left
                // to the container on purpose — TearDownAsync does not touch it.
                Assert.False(watch.IsRunning);
                Assert.False(watchHandlerCalled);
                Assert.True(playbackBoundary.InitializeCount > 0);
                Assert.Equal(0, processRunner.RequestCount);
                Assert.Equal(0, atSpi.StartRequestCount);
                Assert.Equal(0, audioBoundary.OpenCount);
            }
            finally
            {
                if (!providerDisposed)
                {
                    await BoundedTest.WaitAsync(
                        Task.Run(async () => await provider.DisposeAsync())
                    );
                }
            }
        });
    }

    private static ServiceManifestItem[] ValidateServiceManifest(
        ServiceCollection services,
        IServiceProvider provider
    )
    {
        var manifest = services
            .Select(descriptor => new ServiceManifestItem(
                descriptor.ServiceType,
                Classify(descriptor.ServiceType)
            ))
            .ToArray();

        foreach (
            var serviceType in manifest
                .Where(item => item.Classification == ServiceGraphClassification.ResolvedHeadlessly)
                .Select(item => item.ServiceType)
                .Distinct()
        )
        {
            object?[] instances;
            try
            {
                instances = provider.GetServices(serviceType).ToArray();
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    $"Headless manifest resolution failed for {serviceType.FullName}.",
                    ex
                );
            }
            Assert.NotEmpty(instances);
            Assert.All(instances, Assert.NotNull);
        }

        return manifest;
    }

    private static ServiceGraphClassification Classify(Type serviceType)
    {
        if (
            typeof(Window).IsAssignableFrom(serviceType)
            || serviceType.Namespace?.StartsWith(
                "TypeWhisper.Linux.Views",
                StringComparison.Ordinal
            ) == true
            || serviceType == typeof(LearnedCorrectionsToastController)
            || serviceType == typeof(DictationSectionViewModel)
            || serviceType == typeof(WelcomeWizardViewModel)
            || serviceType == typeof(MainWindowViewModel)
        )
        {
            return ServiceGraphClassification.UiOrNativeFenced;
        }

        if (
            serviceType == typeof(ISetupTask)
            || serviceType == typeof(IDeShortcutWriter)
            || serviceType == typeof(IActiveWindowProvider)
            || serviceType == typeof(AtSpiEventClient)
            || serviceType == typeof(IAccessibilityBusActivation)
        )
        {
            return ServiceGraphClassification.ValidatedOnly;
        }

        return ServiceGraphClassification.ResolvedHeadlessly;
    }

    // Checking the parameters is the whole job of an assertion helper, so both are "only used
    // for precondition checks" by construction.
    // ReSharper disable ParameterOnlyUsedForPreconditionCheck.Local
    private static void AssertFenced(
        IEnumerable<ServiceManifestItem> manifest,
        Type serviceType
    )
    {
        Assert.Contains(
            manifest,
            item => item.ServiceType == serviceType
                && item.Classification == ServiceGraphClassification.UiOrNativeFenced
        );
    }
    // ReSharper restore ParameterOnlyUsedForPreconditionCheck.Local

    private static int GetFreeTcpPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }
    }

    private enum ServiceGraphClassification
    {
        ResolvedHeadlessly,
        ValidatedOnly,
        UiOrNativeFenced,
    }

    private sealed record ServiceManifestItem(
        Type ServiceType,
        ServiceGraphClassification Classification
    );
}
