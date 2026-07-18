using TypeWhisper.Linux.Services.Hotkey.DeSetup;

namespace TypeWhisper.Linux.Tests;

internal sealed class FakeDeShortcutWriter : IDeShortcutWriter
{
    public string DesktopId => "fake";
    public string DisplayName => "Fake Desktop";
    public bool SupportsPushToTalk { get; init; }
    public bool RequiresSessionRestartToApply { get; init; }
    public bool IsInstalledResult { get; init; }
    public bool IsManagedShortcutPresentResult { get; set; }
    // Public init-settable knob letting a test disable auto-tracking; no test flips it yet.
    // ReSharper disable once MemberCanBePrivate.Global
    // ReSharper disable once AutoPropertyCanBeMadeGetOnly.Global
    public bool TrackSuccessfulMutations { get; init; } = true;
    public DeShortcutSpec? InstalledSpec { get; set; }
    public DeShortcutWriteResult WriteResult { get; init; } =
        new(true, "Shortcut installed.", []);
    public DeShortcutWriteResult RemoveResult { get; init; } =
        new(true, "Shortcut removed.", []);
    public Exception? IsInstalledException { get; init; }
    // Symmetric fake-configuration hook mirroring IsInstalledException; init set by future tests.
    // ReSharper disable once UnusedAutoPropertyAccessor.Global
    public Exception? IsManagedShortcutPresentException { get; init; }
    public Exception? WriteException { get; init; }
    public Exception? RemoveException { get; init; }
    public Func<DeShortcutSpec, CancellationToken, Task<bool>>? IsInstalledHandler { get; init; }
    // Symmetric fake-configuration hook mirroring IsInstalledHandler; init set by future tests.
    // ReSharper disable once UnusedAutoPropertyAccessor.Global
    public Func<string, CancellationToken, Task<bool>>? IsManagedShortcutPresentHandler
    {
        get;
        init;
    }
    // Symmetric fake-configuration hook mirroring IsInstalledHandler; init set by future tests.
    // ReSharper disable once UnusedAutoPropertyAccessor.Global
    public Func<DeShortcutSpec, CancellationToken, Task<DeShortcutWriteResult>>? WriteHandler
    {
        get;
        init;
    }
    // Symmetric fake-configuration hook mirroring IsInstalledHandler; init set by future tests.
    // ReSharper disable once UnusedAutoPropertyAccessor.Global
    public Func<string, CancellationToken, Task<DeShortcutWriteResult>>? RemoveHandler
    {
        get;
        init;
    }
    public int IsInstalledCallCount { get; private set; }
    public int IsManagedShortcutPresentCallCount { get; private set; }
    public int WriteCallCount { get; private set; }
    // Public assertion hook mirroring WriteCallCount; kept for symmetry even though no test reads it yet.
    // ReSharper disable once MemberCanBePrivate.Global
    public int RemoveCallCount { get; private set; }
    public DeShortcutSpec? LastInstalledSpec { get; private set; }
    public DeShortcutSpec? LastWrittenSpec { get; private set; }
    public string? LastPresenceShortcutId { get; private set; }
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

    public async Task<bool> IsInstalledAsync(DeShortcutSpec spec, CancellationToken ct)
    {
        IsInstalledCallCount++;
        LastInstalledSpec = spec;
        ct.ThrowIfCancellationRequested();
        if (IsInstalledException is not null)
        {
            throw IsInstalledException;
        }

        if (IsInstalledHandler is not null)
        {
            return await IsInstalledHandler(spec, ct);
        }

        return InstalledSpec is null ? IsInstalledResult : InstalledSpec == spec;
    }

    public async Task<bool> IsManagedShortcutPresentAsync(
        string shortcutId,
        CancellationToken ct
    )
    {
        IsManagedShortcutPresentCallCount++;
        LastPresenceShortcutId = shortcutId;
        ct.ThrowIfCancellationRequested();
        if (IsManagedShortcutPresentException is not null)
        {
            throw IsManagedShortcutPresentException;
        }

        if (IsManagedShortcutPresentHandler is not null)
        {
            return await IsManagedShortcutPresentHandler(shortcutId, ct);
        }

        return InstalledSpec is not null || IsManagedShortcutPresentResult;
    }

    public async Task<DeShortcutWriteResult> WriteAsync(
        DeShortcutSpec spec,
        CancellationToken ct
    )
    {
        WriteCallCount++;
        LastWrittenSpec = spec;
        ct.ThrowIfCancellationRequested();
        if (WriteException is not null)
        {
            throw WriteException;
        }

        var result = WriteHandler is null
            ? WriteResult
            : await WriteHandler(spec, ct);
        if (!result.Success || !TrackSuccessfulMutations)
        {
            return result;
        }

        InstalledSpec = spec;
        IsManagedShortcutPresentResult = true;
        return result;
    }

    public async Task<DeShortcutWriteResult> RemoveAsync(
        string shortcutId,
        CancellationToken ct
    )
    {
        RemoveCallCount++;
        LastRemovedShortcutId = shortcutId;
        ct.ThrowIfCancellationRequested();
        if (RemoveException is not null)
        {
            throw RemoveException;
        }

        var result = RemoveHandler is null
            ? RemoveResult
            : await RemoveHandler(shortcutId, ct);
        if (!result.Success || !TrackSuccessfulMutations)
        {
            return result;
        }

        InstalledSpec = null;
        IsManagedShortcutPresentResult = false;
        return result;
    }
}
