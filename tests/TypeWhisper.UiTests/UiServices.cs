using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TypeWhisper.Core;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Integration.Tests.TestDoubles;
using TypeWhisper.Linux;
using TypeWhisper.Linux.Services;
using TypeWhisper.Linux.Services.ActiveWindow;
using TypeWhisper.Linux.Services.Hotkey;
using TypeWhisper.Linux.Services.Hotkey.Evdev;
using TypeWhisper.Linux.Services.Insertion;
using TypeWhisper.Linux.Services.Plugins;

namespace TypeWhisper.UiTests;

/// <summary>
///     The production service registrations with the same external boundaries replaced as the
///     integration suite, so views bind to real ViewModels without touching the desktop.
/// </summary>
internal static class UiServices
{
    internal static ServiceProvider Build()
    {
        TypeWhisperEnvironment.EnsureDirectories();
        var services = new ServiceCollection();
        ServiceRegistrations.Register(services);

        var insertion = new RecordingTextInsertionPlatform();
        var audio = new RecordingAudioBoundary();
        var playback = new RecordingPlaybackBoundary();
        var systemAudio = new RecordingSystemAudio();
        services.Replace(ServiceDescriptor.Singleton<IProcessRunner>(new HeadlessProcessRunner()));
        services.Replace(ServiceDescriptor.Singleton<IAudioDuckingService>(systemAudio));
        services.Replace(ServiceDescriptor.Singleton<IMediaPauseService>(systemAudio));
        services.Replace(ServiceDescriptor.Singleton<IDefaultDeviceChangeWatcher>(new HeadlessDefaultDeviceWatcher()));
        services.Replace(ServiceDescriptor.Singleton<ISessionActivityMonitor>(new HeadlessSessionActivityMonitor()));
        services.Replace(ServiceDescriptor.Singleton<IAtSpiEventClient>(new HeadlessAtSpiClient()));
        services.Replace(ServiceDescriptor.Singleton(new BackendSelector(static () => new HeadlessShortcutBackend())));
        services.Replace(ServiceDescriptor.Singleton<AudioRecordingService>(sp =>
            audio.CreateService(sp.GetRequiredService<IErrorLogService>())));
        services.Replace(ServiceDescriptor.Singleton(playback.CreateService()));
        services.Replace(ServiceDescriptor.Singleton<TextInsertionService>(sp => new TextInsertionService(
            insertion,
            sp.GetRequiredService<IErrorLogService>(),
            sp.GetRequiredService<IPasteConfirmationSource>(),
            atSpiClient: sp.GetRequiredService<IAtSpiEventClient>(),
            learningConsent: () => false)));
        services.Replace(ServiceDescriptor.Singleton<SpeechFeedbackService>(sp => new SpeechFeedbackService(
            sp.GetRequiredService<ISettingsService>(),
            sp.GetRequiredService<PluginManager>(),
            new UnconfiguredTtsProvider())));
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
    }
}
