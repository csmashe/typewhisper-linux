using TypeWhisper.Linux.Services.Hotkey.DeSetup;

namespace TypeWhisper.Linux.Tests;

internal sealed class FakeDeShortcutWriter : IDeShortcutWriter
{
    public string DesktopId => "fake";
    public string DisplayName => "Fake Desktop";
    public bool SupportsPushToTalk { get; init; }
    public bool RequiresSessionRestartToApply => false;
    public int WriteCallCount { get; private set; }
    // Public assertion hook mirroring WriteCallCount; kept for symmetry even though no test reads it yet.
    // ReSharper disable once MemberCanBePrivate.Global
    public int RemoveCallCount { get; private set; }
    public DeShortcutSpec? LastWrittenSpec { get; private set; }

    public bool IsCurrentDesktop()
    {
        return true;
    }

    public string PreviewLines(DeShortcutSpec spec)
    {
        return $"preview: {spec.OnPressCommand}";
    }

    public Task<bool> IsInstalledAsync(DeShortcutSpec spec, CancellationToken ct)
    {
        return Task.FromResult(false);
    }

    public Task<DeShortcutWriteResult> WriteAsync(DeShortcutSpec spec, CancellationToken ct)
    {
        WriteCallCount++;
        LastWrittenSpec = spec;
        return Task.FromResult(
            new DeShortcutWriteResult(true, "Shortcut installed.", [])
        );
    }

    public Task<DeShortcutWriteResult> RemoveAsync(string shortcutId, CancellationToken ct)
    {
        RemoveCallCount++;
        return Task.FromResult(
            new DeShortcutWriteResult(true, "Shortcut removed.", [])
        );
    }
}
