using TypeWhisper.Linux.Services.Hotkey.DeSetup;

namespace TypeWhisper.Linux.Tests;

internal sealed class FakeDeShortcutWriter : IDeShortcutWriter
{
    public string DesktopId => "fake";
    public string DisplayName => "Fake Desktop";
    public bool SupportsPushToTalk { get; init; }
    public bool RequiresSessionRestartToApply { get; init; }
    public bool IsInstalledResult { get; init; }
    public DeShortcutWriteResult WriteResult { get; init; } =
        new(true, "Shortcut installed.", []);
    public DeShortcutWriteResult RemoveResult { get; init; } =
        new(true, "Shortcut removed.", []);
    public Exception? IsInstalledException { get; init; }
    public Exception? WriteException { get; init; }
    public Exception? RemoveException { get; init; }
    public int IsInstalledCallCount { get; private set; }
    public int WriteCallCount { get; private set; }
    // Public assertion hook mirroring WriteCallCount; kept for symmetry even though no test reads it yet.
    // ReSharper disable once MemberCanBePrivate.Global
    public int RemoveCallCount { get; private set; }
    public DeShortcutSpec? LastInstalledSpec { get; private set; }
    public DeShortcutSpec? LastWrittenSpec { get; private set; }
    // Assertion hook mirroring LastInstalledSpec/LastWrittenSpec; kept for symmetry even though no test reads it yet.
    // ReSharper disable once UnusedAutoPropertyAccessor.Global
    public string? LastRemovedShortcutId { get; private set; }

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
        IsInstalledCallCount++;
        LastInstalledSpec = spec;
        ct.ThrowIfCancellationRequested();
        return IsInstalledException is null
            ? Task.FromResult(IsInstalledResult)
            : Task.FromException<bool>(IsInstalledException);
    }

    public Task<DeShortcutWriteResult> WriteAsync(DeShortcutSpec spec, CancellationToken ct)
    {
        WriteCallCount++;
        LastWrittenSpec = spec;
        ct.ThrowIfCancellationRequested();
        return WriteException is null
            ? Task.FromResult(WriteResult)
            : Task.FromException<DeShortcutWriteResult>(WriteException);
    }

    public Task<DeShortcutWriteResult> RemoveAsync(string shortcutId, CancellationToken ct)
    {
        RemoveCallCount++;
        LastRemovedShortcutId = shortcutId;
        ct.ThrowIfCancellationRequested();
        return RemoveException is null
            ? Task.FromResult(RemoveResult)
            : Task.FromException<DeShortcutWriteResult>(RemoveException);
    }
}
