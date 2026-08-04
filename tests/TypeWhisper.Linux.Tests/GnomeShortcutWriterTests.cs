using TypeWhisper.Linux.Services;
using TypeWhisper.Linux.Services.Hotkey.DeSetup;
using TypeWhisper.Tests;
using Xunit;

namespace TypeWhisper.Linux.Tests;

/// <summary>
///     Highest-risk surface in Phase 6 per the phase spec: the GNOME
///     custom-keybindings list parser. A bug here can silently overwrite
///     the user's other custom shortcuts. The tests cover every shape
///     gsettings is documented (or observed in the wild) to emit, plus a
///     few hand-edited-via-dconf-editor variants.
/// </summary>
public sealed class GnomeShortcutWriterTests : IDisposable
{
    private const string ConcurrentPath = "/org/example/custom-keybindings/concurrent/";
    private const string ShortcutId = "typewhisper.dictation.toggle";
    private readonly string? _originalPath = Environment.GetEnvironmentVariable("PATH");
    private readonly string _tempDirectory = TestPaths.CreateTempDirectory(
        "gnome-shortcut-writer"
    );

    public GnomeShortcutWriterTests()
    {
        var binDirectory = Path.Join(_tempDirectory, "bin");
        Directory.CreateDirectory(binDirectory);
        var executable = Path.Join(binDirectory, "gsettings");
        File.WriteAllText(executable, "#!/bin/sh\nexit 0\n");
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(
                executable,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
            );
        }

        Environment.SetEnvironmentVariable("PATH", binDirectory);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("PATH", _originalPath);
        try
        {
            TestPaths.DeleteDirectory(_tempDirectory);
        }
        catch
        {
            // Best-effort cleanup for temp test directories.
        }
    }

    [Fact]
    public void ParseGSettingsList_Empty_TypedAnnotation_ReturnsEmptyList()
    {
        var result = GnomeShortcutWriter.ParseGSettingsList("@as []");
        Assert.Empty(result);
    }

    [Fact]
    public void ParseGSettingsList_Empty_BareBrackets_ReturnsEmptyList()
    {
        var result = GnomeShortcutWriter.ParseGSettingsList("[]");
        Assert.Empty(result);
    }

    [Fact]
    public void ParseGSettingsList_SingleEntry_ReturnsOneItem()
    {
        var result = GnomeShortcutWriter.ParseGSettingsList(
            "['/org/gnome/settings-daemon/plugins/media-keys/custom-keybindings/custom0/']"
        );
        Assert.Single(result);
        Assert.Equal(
            "/org/gnome/settings-daemon/plugins/media-keys/custom-keybindings/custom0/",
            result[0]
        );
    }

    [Fact]
    public void ParseGSettingsList_MultipleEntries_ReturnsAll()
    {
        const string raw = "['/org/.../custom0/', '/org/.../custom1/', '/org/.../custom2/']";
        var result = GnomeShortcutWriter.ParseGSettingsList(raw);
        Assert.Equal(3, result.Count);
        Assert.Equal("/org/.../custom0/", result[0]);
        Assert.Equal("/org/.../custom1/", result[1]);
        Assert.Equal("/org/.../custom2/", result[2]);
    }

    [Fact]
    public void ParseGSettingsList_HandlesTrailingNewline()
    {
        // gsettings always appends "\n" — parser must tolerate it.
        var result = GnomeShortcutWriter.ParseGSettingsList("['/a/', '/b/']\n");
        Assert.Equal(2, result.Count);
    }

    [Fact]
    public void ParseGSettingsList_HandlesEscapedSingleQuote()
    {
        // A user-renamed entry could contain an apostrophe — gsettings
        // escapes it as \'.
        var result = GnomeShortcutWriter.ParseGSettingsList(@"['Chris\'s key', '/b/']");
        Assert.Equal(2, result.Count);
        Assert.Equal("Chris's key", result[0]);
    }

    [Fact]
    public void ParseGSettingsList_HandlesDoubleQuotedEntries()
    {
        // Hand-edited via dconf-editor — double quotes are accepted.
        var result = GnomeShortcutWriter.ParseGSettingsList("[\"/a/\", \"/b/\"]");
        Assert.Equal(2, result.Count);
        Assert.Equal("/a/", result[0]);
        Assert.Equal("/b/", result[1]);
    }

    [Fact]
    public void ParseGSettingsList_TolerantOfExtraWhitespace()
    {
        var result = GnomeShortcutWriter.ParseGSettingsList("  [  '/a/' ,  '/b/'  ]  ");
        Assert.Equal(2, result.Count);
    }

    [Fact]
    public void ParseGSettingsList_RejectsMalformedShape()
    {
        Assert.Throws<FormatException>(() => GnomeShortcutWriter.ParseGSettingsList("notalist"));
    }

    [Fact]
    public void ParseGSettingsList_RejectsBlankInput_FailClosed()
    {
        // Critical data-loss guard: an empty stdout from gsettings is
        // anomalous, not an empty list. Treating it as empty would let
        // us overwrite real user shortcuts on the very next set.
        Assert.Throws<FormatException>(() => GnomeShortcutWriter.ParseGSettingsList(""));
        Assert.Throws<FormatException>(() => GnomeShortcutWriter.ParseGSettingsList("   "));
        Assert.Throws<FormatException>(() => GnomeShortcutWriter.ParseGSettingsList("\n"));
    }

    [Fact]
    public void ParseGSettingsList_RejectsUnknownEscape()
    {
        // \n inside a single-quoted entry is not something gsettings
        // emits — accepting it would let us silently rewrite the
        // user's entry on round-trip.
        Assert.Throws<FormatException>(() =>
            GnomeShortcutWriter.ParseGSettingsList(@"['foo\nbar']")
        );
    }

    [Fact]
    public void ParseGSettingsList_RejectsUnterminatedQuote()
    {
        // Critical: refuse rather than silently dropping the entry,
        // because returning an incomplete list would cause the writer
        // to wipe out a real entry on round-trip.
        Assert.Throws<FormatException>(() => GnomeShortcutWriter.ParseGSettingsList("['/a/"));
    }

    [Fact]
    public void FormatGSettingsList_Empty_ReturnsBareBrackets()
    {
        Assert.Equal("[]", GnomeShortcutWriter.FormatGSettingsList([]));
    }

    [Fact]
    public void FormatGSettingsList_SingleEntry_RoundTripsThroughParser()
    {
        var input = new[] { "/org/.../typewhisper-abcd1234/" };
        var formatted = GnomeShortcutWriter.FormatGSettingsList(input);
        var parsed = GnomeShortcutWriter.ParseGSettingsList(formatted);
        Assert.Equal(input, parsed);
    }

    [Fact]
    public void FormatGSettingsList_EscapesSingleQuotesAndBackslashes()
    {
        var input = new[] { "Chris's key", @"path\with\backslash" };
        var formatted = GnomeShortcutWriter.FormatGSettingsList(input);
        var parsed = GnomeShortcutWriter.ParseGSettingsList(formatted);
        Assert.Equal(input, parsed);
    }

    [Fact]
    public void FormatGnomeAccel_ProducesGtkAcceleratorFormat()
    {
        Assert.Equal(
            "<Control><Shift>space",
            GnomeShortcutWriter.FormatGnomeAccel("Ctrl+Shift+Space")
        );
        Assert.Equal("<Control><Alt>F9", GnomeShortcutWriter.FormatGnomeAccel("Ctrl+Alt+F9"));
        Assert.Equal("<Super>k", GnomeShortcutWriter.FormatGnomeAccel("Super+K"));
    }

    [Fact]
    public void FormatGnomeAccel_MapsMetaToSuper()
    {
        Assert.Equal("<Super>k", GnomeShortcutWriter.FormatGnomeAccel("Meta+K"));
    }

    [Fact]
    public void FormatGnomeAccel_EmptyInput_ReturnsEmpty()
    {
        Assert.Equal(string.Empty, GnomeShortcutWriter.FormatGnomeAccel(""));
    }

    [Fact]
    public async Task WriteAsync_AllGSettingsCallsUseFiveSecondTimeoutAndCallerToken()
    {
        var runner = new StatefulGSettingsRunner("[]");
        var writer = new GnomeShortcutWriter(
            runner,
            Path.Join(_tempDirectory, "bounded-write-backups")
        );
        using var callerCts = new CancellationTokenSource();

        var result = await writer.WriteAsync(CreateSpec(), callerCts.Token);

        Assert.True(result.Success);
        AssertGSettingsContract(runner, callerCts.Token);
        Assert.Equal(2, runner.Invocations.Count(call => IsListGet(call.Args)));
        Assert.Single(runner.Invocations, call => IsListSet(call.Args));
        Assert.Equal(
            3,
            runner.Invocations.Count(call => call.Args is ["set", _, var key, _]
                                                     && key != "custom-keybindings")
        );
    }

    [Fact]
    public async Task RemoveAsync_AllGSettingsCallsUseFiveSecondTimeoutAndCallerToken()
    {
        var spec = CreateSpec();
        var probe = new GnomeShortcutWriter(
            new StatefulGSettingsRunner("[]"),
            Path.Join(_tempDirectory, "bounded-remove-probe-backups")
        );
        var managedPath = GetManagedPath(probe, spec);
        var runner = new StatefulGSettingsRunner(
            GnomeShortcutWriter.FormatGSettingsList([managedPath])
        );
        var writer = new GnomeShortcutWriter(
            runner,
            Path.Join(_tempDirectory, "bounded-remove-backups")
        );
        using var callerCts = new CancellationTokenSource();

        var result = await writer.RemoveAsync(ShortcutId, callerCts.Token);

        Assert.True(result.Success);
        AssertGSettingsContract(runner, callerCts.Token);
        Assert.Equal(2, runner.Invocations.Count(call => IsListGet(call.Args)));
        Assert.Single(runner.Invocations, call => IsListSet(call.Args));
        Assert.Equal(
            3,
            runner.Invocations.Count(call => call.Args is ["reset", _, _])
        );
    }

    [Fact]
    public async Task StatusProbes_AllGSettingsCallsUseFiveSecondTimeoutAndCallerToken()
    {
        var runner = new StatefulGSettingsRunner("[]");
        var writer = new GnomeShortcutWriter(
            runner,
            Path.Join(_tempDirectory, "bounded-probe-backups")
        );
        var spec = CreateSpec();
        var write = await writer.WriteAsync(spec, CancellationToken.None);
        Assert.True(write.Success);
        runner.Invocations.Clear();
        using var callerCts = new CancellationTokenSource();

        Assert.True(await writer.IsInstalledAsync(spec, callerCts.Token));
        Assert.True(
            await writer.IsManagedShortcutPresentAsync(ShortcutId, callerCts.Token)
        );

        AssertGSettingsContract(runner, callerCts.Token);
        Assert.Equal(2, runner.Invocations.Count(call => IsListGet(call.Args)));
        Assert.Equal(
            2,
            runner.Invocations.Count(call => call.Args is ["get", _, var key]
                                                     && key != "custom-keybindings")
        );
    }

    [Fact]
    public async Task WriteAsync_GSettingsTimeoutFailsPromptlyWithTimeoutDetail()
    {
        var runner = new StatefulGSettingsRunner(
            "[]",
            resultOverride: args => IsListSet(args)
                ? new ProcessRunResult(
                    true,
                    true,
                    -1,
                    string.Empty,
                    string.Empty
                )
                : null
        );
        var writer = new GnomeShortcutWriter(
            runner,
            Path.Join(_tempDirectory, "timed-out-write-backups")
        );
        using var callerCts = new CancellationTokenSource();

        // ReSharper disable once MethodSupportsCancellation -- deliberate hard test-timeout guard; the caller token already flows into WriteAsync, and tying the wait to it would defeat the fail-fast bound.
        var result = await writer.WriteAsync(CreateSpec(), callerCts.Token)
            .WaitAsync(TimeSpan.FromSeconds(2));

        Assert.False(result.Success);
        Assert.Contains("gsettings timed out after 5 seconds.", result.UserMessage);
        AssertGSettingsContract(runner, callerCts.Token);
        Assert.Single(runner.Invocations, call => IsListSet(call.Args));
    }

    [Fact]
    public async Task WriteAsync_ConcurrentListAddition_RetriesMergeAndBacksUpLatestList()
    {
        var runner = new StatefulGSettingsRunner(
            GnomeShortcutWriter.FormatGSettingsList(["/old-a/", "/old-b/"]),
            MutationMode.AfterFirstGet
        );
        var backupDirectory = Path.Join(_tempDirectory, "install-backups");
        var writer = new GnomeShortcutWriter(runner, backupDirectory);
        var spec = CreateSpec();
        var managedPath = GetManagedPath(writer, spec);

        var result = await writer.WriteAsync(spec, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(
            new[] { "/old-a/", "/old-b/", ConcurrentPath, managedPath },
            GnomeShortcutWriter.ParseGSettingsList(runner.CurrentRaw)
        );
        Assert.Equal(1, runner.WholeListSetCount);
        Assert.Equal(3, runner.PropertySetCount);
        var backup = Assert.Single(Directory.EnumerateFiles(backupDirectory, "*.txt"));
        Assert.Contains(ConcurrentPath, await File.ReadAllTextAsync(backup));
    }

    [Fact]
    public async Task WriteAsync_PropertySetFailure_UnpublishesThePathItJustAdded()
    {
        var runner = new StatefulGSettingsRunner(
            GnomeShortcutWriter.FormatGSettingsList(["/old-a/"]),
            resultOverride: args => args is ["set", _, "command", _]
                ? new ProcessRunResult(true, false, 1, string.Empty, "dconf write failed")
                : null
        );
        var writer = new GnomeShortcutWriter(
            runner,
            Path.Join(_tempDirectory, "rollback-backups")
        );

        var result = await writer.WriteAsync(CreateSpec(), CancellationToken.None);

        Assert.False(result.Success);
        // The half-written entry must not stay listed, or GNOME shows an empty custom shortcut.
        Assert.Equal(
            "/old-a/",
            Assert.Single(GnomeShortcutWriter.ParseGSettingsList(runner.CurrentRaw))
        );
    }

    [Fact]
    public async Task WriteAsync_PropertySetFailure_KeepsAnEntryAnotherWriterFullyConfigured()
    {
        var spec = CreateSpec();
        var runner = new StatefulGSettingsRunner(
            GnomeShortcutWriter.FormatGSettingsList(["/old-a/"]),
            resultOverride: args => args switch
            {
                ["set", _, "command", _] =>
                    new ProcessRunResult(true, false, 1, string.Empty, "dconf write failed"),
                // A competing install completed between our failed set and the cleanup.
                ["get", _, "command"] =>
                    new ProcessRunResult(true, false, 0, $"'{spec.OnPressCommand}'", string.Empty),
                ["get", _, "binding"] => new ProcessRunResult(
                    true,
                    false,
                    0,
                    $"'{GnomeShortcutWriter.FormatGnomeAccel(spec.Trigger)}'",
                    string.Empty
                ),
                _ => null
            }
        );
        var writer = new GnomeShortcutWriter(
            runner,
            Path.Join(_tempDirectory, "competing-install-backups")
        );
        var managedPath = GetManagedPath(writer, spec);

        var result = await writer.WriteAsync(spec, CancellationToken.None);

        Assert.False(result.Success);
        // Rolling back would have deleted the other caller's working shortcut.
        Assert.Contains(managedPath, GnomeShortcutWriter.ParseGSettingsList(runner.CurrentRaw));
        Assert.Null(result.Warning);
    }

    [Fact]
    public async Task WriteAsync_RollbackListSetFails_WarnsThatTheEntryIsStillListed()
    {
        var listSets = 0;
        var runner = new StatefulGSettingsRunner(
            GnomeShortcutWriter.FormatGSettingsList(["/old-a/"]),
            resultOverride: args =>
            {
                if (args is ["set", _, "command", _])
                {
                    return new ProcessRunResult(true, false, 1, string.Empty, "dconf write failed");
                }

                // The same outage that broke the property write breaks the cleanup's list write.
                return IsListSet(args) && ++listSets == 2
                    ? new ProcessRunResult(true, false, 1, string.Empty, "dconf list write failed")
                    : null;
            }
        );
        var writer = new GnomeShortcutWriter(
            runner,
            Path.Join(_tempDirectory, "rollback-set-fail-backups")
        );
        var managedPath = GetManagedPath(writer, CreateSpec());

        var result = await writer.WriteAsync(CreateSpec(), CancellationToken.None);

        Assert.False(result.Success);
        Assert.NotNull(result.Warning);
        Assert.Contains(managedPath, result.Warning);
        Assert.Contains(managedPath, GnomeShortcutWriter.ParseGSettingsList(runner.CurrentRaw));
    }

    [Fact]
    public async Task WriteAsync_RollbackListReadFails_WarnsThatTheEntryIsStillListed()
    {
        var listGets = 0;
        var runner = new StatefulGSettingsRunner(
            GnomeShortcutWriter.FormatGSettingsList(["/old-a/"]),
            resultOverride: args =>
            {
                if (args is ["set", _, "command", _])
                {
                    return new ProcessRunResult(true, false, 1, string.Empty, "dconf write failed");
                }

                // The add's read and confirm succeed; the cleanup's read does not.
                return IsListGet(args) && ++listGets > 2
                    ? new ProcessRunResult(true, false, 1, string.Empty, "dconf read failed")
                    : null;
            }
        );
        var writer = new GnomeShortcutWriter(
            runner,
            Path.Join(_tempDirectory, "rollback-read-fail-backups")
        );
        var managedPath = GetManagedPath(writer, CreateSpec());

        var result = await writer.WriteAsync(CreateSpec(), CancellationToken.None);

        Assert.False(result.Success);
        Assert.NotNull(result.Warning);
        Assert.Contains(managedPath, result.Warning);
        Assert.Contains(managedPath, GnomeShortcutWriter.ParseGSettingsList(runner.CurrentRaw));
    }

    [Fact]
    public async Task WriteAsync_PropertySetFailure_LeavesAPreExistingPathListed()
    {
        var spec = CreateSpec();
        var pathProbe = new GnomeShortcutWriter(
            new StatefulGSettingsRunner("[]"),
            Path.Join(_tempDirectory, "pre-existing-probe-backups")
        );
        var managedPath = GetManagedPath(pathProbe, spec);
        var runner = new StatefulGSettingsRunner(
            GnomeShortcutWriter.FormatGSettingsList(["/old-a/", managedPath]),
            resultOverride: args => args is ["set", _, "command", _]
                ? new ProcessRunResult(true, false, 1, string.Empty, "dconf write failed")
                : null
        );
        var writer = new GnomeShortcutWriter(
            runner,
            Path.Join(_tempDirectory, "pre-existing-backups")
        );

        var result = await writer.WriteAsync(spec, CancellationToken.None);

        Assert.False(result.Success);
        // This call didn't publish the path, so the user's existing entry stays put.
        Assert.Equal(
            new[] { "/old-a/", managedPath },
            GnomeShortcutWriter.ParseGSettingsList(runner.CurrentRaw)
        );
    }

    [Fact]
    public async Task RemoveAsync_DuplicatedManagedPath_RemovesEveryOccurrence()
    {
        var spec = CreateSpec();
        var pathProbe = new GnomeShortcutWriter(
            new StatefulGSettingsRunner("[]"),
            Path.Join(_tempDirectory, "duplicate-probe-backups")
        );
        var managedPath = GetManagedPath(pathProbe, spec);
        // An external edit (dconf-editor, a restored backup) can list the path twice.
        var runner = new StatefulGSettingsRunner(
            GnomeShortcutWriter.FormatGSettingsList([managedPath, "/old-a/", managedPath])
        );
        var writer = new GnomeShortcutWriter(
            runner,
            Path.Join(_tempDirectory, "duplicate-backups")
        );

        var result = await writer.RemoveAsync(ShortcutId, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(
            "/old-a/",
            Assert.Single(GnomeShortcutWriter.ParseGSettingsList(runner.CurrentRaw))
        );
    }

    [Fact]
    public async Task RemoveAsync_ConcurrentListAddition_RetriesMergeAndBacksUpLatestList()
    {
        var spec = CreateSpec();
        var pathProbe = new GnomeShortcutWriter(
            new StatefulGSettingsRunner("[]"),
            Path.Join(_tempDirectory, "probe-backups")
        );
        var managedPath = GetManagedPath(pathProbe, spec);
        var runner = new StatefulGSettingsRunner(
            GnomeShortcutWriter.FormatGSettingsList(["/old-a/", managedPath, "/old-b/"]),
            MutationMode.AfterFirstGet
        );
        var backupDirectory = Path.Join(_tempDirectory, "remove-backups");
        var writer = new GnomeShortcutWriter(runner, backupDirectory);

        var result = await writer.RemoveAsync(ShortcutId, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(
            new[] { "/old-a/", "/old-b/", ConcurrentPath },
            GnomeShortcutWriter.ParseGSettingsList(runner.CurrentRaw)
        );
        Assert.Equal(1, runner.WholeListSetCount);
        Assert.Equal(3, runner.PropertyResetCount);
        var backup = Assert.Single(Directory.EnumerateFiles(backupDirectory, "*.txt"));
        Assert.Contains(ConcurrentPath, await File.ReadAllTextAsync(backup));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ListMutation_RepeatedConcurrentChanges_RefusesAllSetsAndResets(
        bool remove
    )
    {
        var spec = CreateSpec();
        var pathProbe = new GnomeShortcutWriter(
            new StatefulGSettingsRunner("[]"),
            Path.Join(_tempDirectory, "probe-backups")
        );
        var managedPath = GetManagedPath(pathProbe, spec);
        var initialPaths = remove
            ? new[] { "/old/", managedPath }
            : new[] { "/old/" };
        var runner = new StatefulGSettingsRunner(
            GnomeShortcutWriter.FormatGSettingsList(initialPaths),
            MutationMode.AfterEveryGet
        );
        var backupDirectory = Path.Join(_tempDirectory, "conflict-backups");
        var writer = new GnomeShortcutWriter(runner, backupDirectory);

        var result = remove
            ? await writer.RemoveAsync(ShortcutId, CancellationToken.None)
            : await writer.WriteAsync(spec, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("Please retry", result.UserMessage);
        Assert.Equal(0, runner.WholeListSetCount);
        Assert.Equal(0, runner.PropertySetCount);
        Assert.Equal(0, runner.PropertyResetCount);
        Assert.False(Directory.Exists(backupDirectory));
    }

    [Fact]
    public async Task WriteAsync_PathAlreadyListed_SkipsWholeListSetButUpdatesProperties()
    {
        var spec = CreateSpec();
        var probe = new GnomeShortcutWriter(
            new StatefulGSettingsRunner("[]"),
            Path.Join(_tempDirectory, "probe-backups")
        );
        var managedPath = GetManagedPath(probe, spec);
        var runner = new StatefulGSettingsRunner(
            GnomeShortcutWriter.FormatGSettingsList(["/old/", managedPath])
        );
        var backupDirectory = Path.Join(_tempDirectory, "no-op-install-backups");
        var writer = new GnomeShortcutWriter(runner, backupDirectory);

        var result = await writer.WriteAsync(spec, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(0, runner.WholeListSetCount);
        Assert.Equal(3, runner.PropertySetCount);
        Assert.False(Directory.Exists(backupDirectory));
    }

    [Fact]
    public async Task RemoveAsync_PathAlreadyAbsent_IsSuccessfulNoOpWithoutResets()
    {
        var runner = new StatefulGSettingsRunner(
            GnomeShortcutWriter.FormatGSettingsList(["/old/"])
        );
        var backupDirectory = Path.Join(_tempDirectory, "no-op-remove-backups");
        var writer = new GnomeShortcutWriter(runner, backupDirectory);

        var result = await writer.RemoveAsync(ShortcutId, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(0, runner.WholeListSetCount);
        Assert.Equal(0, runner.PropertyResetCount);
        Assert.False(Directory.Exists(backupDirectory));
    }

    [Fact]
    public async Task WriteAsync_MalformedList_FailsClosedAndPreservesRawBackup()
    {
        const string malformed = "not-a-list";
        var runner = new StatefulGSettingsRunner(malformed);
        var backupDirectory = Path.Join(_tempDirectory, "malformed-backups");
        var writer = new GnomeShortcutWriter(runner, backupDirectory);

        var result = await writer.WriteAsync(CreateSpec(), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(0, runner.WholeListSetCount);
        Assert.Equal(0, runner.PropertySetCount);
        var backup = Assert.Single(Directory.EnumerateFiles(backupDirectory, "*.txt"));
        Assert.Contains(malformed, await File.ReadAllTextAsync(backup));
    }

    [Fact]
    public async Task ManagedPresence_DistinguishesAbsentCurrentAndStaleWithoutWriting()
    {
        var runner = new StatefulGSettingsRunner("[]");
        var writer = new GnomeShortcutWriter(
            runner,
            Path.Join(_tempDirectory, "presence-backups")
        );
        var installed = CreateSpec();
        var changed = installed with { Trigger = "Alt+F8" };

        Assert.False(
            await writer.IsManagedShortcutPresentAsync(ShortcutId, CancellationToken.None)
        );
        Assert.False(await writer.IsInstalledAsync(installed, CancellationToken.None));

        var write = await writer.WriteAsync(installed, CancellationToken.None);

        Assert.True(write.Success);
        Assert.True(
            await writer.IsManagedShortcutPresentAsync(ShortcutId, CancellationToken.None)
        );
        Assert.True(await writer.IsInstalledAsync(installed, CancellationToken.None));
        Assert.False(await writer.IsInstalledAsync(changed, CancellationToken.None));
        Assert.True(
            await writer.IsManagedShortcutPresentAsync(ShortcutId, CancellationToken.None)
        );
    }

    private static DeShortcutSpec CreateSpec()
    {
        return new DeShortcutSpec(
            ShortcutId,
            "TypeWhisper Dictation",
            "Ctrl+Shift+Space",
            "typewhisper record toggle",
            null,
            null,
            null
        );
    }

    private static string GetManagedPath(GnomeShortcutWriter writer, DeShortcutSpec spec)
    {
        const string prefix = "gsettings list path: ";
        var firstLine = writer.PreviewLines(spec).Split('\n')[0];
        Assert.StartsWith(prefix, firstLine);
        return firstLine[prefix.Length..];
    }

    private static void AssertGSettingsContract(
        StatefulGSettingsRunner runner,
        CancellationToken callerToken
    )
    {
        Assert.NotEmpty(runner.Invocations);
        Assert.All(
            runner.Invocations,
            invocation =>
            {
                Assert.Equal("gsettings", invocation.FileName);
                Assert.Equal(TimeSpan.FromSeconds(5), invocation.Timeout);
                Assert.Equal(callerToken, invocation.CancellationToken);
            }
        );
    }

    private static bool IsListGet(IReadOnlyList<string> args)
    {
        return args is ["get", _, "custom-keybindings"];
    }

    private static bool IsListSet(IReadOnlyList<string> args)
    {
        return args is ["set", _, "custom-keybindings", _];
    }

    private enum MutationMode
    {
        None,
        AfterFirstGet,
        AfterEveryGet,
    }

    private sealed class StatefulGSettingsRunner : IProcessRunner
    {
        private readonly string _concurrentPath;
        private readonly MutationMode _mutationMode;
        private readonly Func<IReadOnlyList<string>, ProcessRunResult?>? _resultOverride;
        private readonly Dictionary<(string Schema, string Key), string> _properties = [];
        private int _listGetCount;

        public StatefulGSettingsRunner(
            string initialRaw,
            MutationMode mutationMode = MutationMode.None,
            string concurrentPath = ConcurrentPath,
            Func<IReadOnlyList<string>, ProcessRunResult?>? resultOverride = null
        )
        {
            CurrentRaw = initialRaw;
            _mutationMode = mutationMode;
            _concurrentPath = concurrentPath;
            _resultOverride = resultOverride;
        }

        public string CurrentRaw { get; private set; }
        public List<Invocation> Invocations { get; } = [];
        public int PropertyResetCount { get; private set; }
        public int PropertySetCount { get; private set; }
        public int WholeListSetCount { get; private set; }

        public Task<ProcessRunResult> RunAsync(
            string fileName,
            IReadOnlyList<string> args,
            IReadOnlyDictionary<string, string>? environment = null,
            string? standardInput = null,
            TimeSpan? timeout = null,
            bool detachAfterExit = false,
            CancellationToken ct = default
        )
        {
            Invocations.Add(new Invocation(fileName, args.ToArray(), timeout, ct));
            ct.ThrowIfCancellationRequested();
            Assert.Equal("gsettings", fileName);
            var overridden = _resultOverride?.Invoke(args);
            if (overridden is not null)
            {
                return Task.FromResult(overridden);
            }

            if (IsListGet(args))
            {
                _listGetCount++;
                var returnedRaw = CurrentRaw;
                // ReSharper disable once InvertIf -- inverting would duplicate the return of the pre-mutation snapshot; the current shape keeps the "mutate then return the earlier value" intent clear.
                if (
                    _mutationMode == MutationMode.AfterEveryGet
                    || (_mutationMode == MutationMode.AfterFirstGet && _listGetCount == 1)
                )
                {
                    var concurrent = _mutationMode == MutationMode.AfterEveryGet
                        ? $"/org/example/custom-keybindings/concurrent-{_listGetCount}/"
                        : _concurrentPath;
                    var paths = GnomeShortcutWriter.ParseGSettingsList(CurrentRaw);
                    paths.Add(concurrent);
                    CurrentRaw = GnomeShortcutWriter.FormatGSettingsList(paths);
                }

                return Task.FromResult(Success(returnedRaw));
            }

            if (args is ["get", var schema, var key])
            {
                return Task.FromResult(
                    Success(
                        _properties.TryGetValue((schema, key), out var value)
                            ? $"'{value}'"
                            : "''"
                    )
                );
            }

            // ReSharper disable once ConvertIfStatementToSwitchStatement -- the branches mix a list-pattern helper with args[0] guards; a switch would not read more clearly.
            if (IsListSet(args))
            {
                WholeListSetCount++;
                CurrentRaw = args[3];
            }
            else if (args.Count > 0 && args[0] == "set")
            {
                PropertySetCount++;
                _properties[(args[1], args[2])] = args[3];
            }
            else if (args.Count > 0 && args[0] == "reset")
            {
                PropertyResetCount++;
                _properties.Remove((args[1], args[2]));
            }

            return Task.FromResult(Success());
        }

        private static ProcessRunResult Success(string stdout = "")
        {
            return new ProcessRunResult(true, false, 0, stdout, string.Empty);
        }

        public sealed record Invocation(
            string FileName,
            IReadOnlyList<string> Args,
            TimeSpan? Timeout,
            CancellationToken CancellationToken
        );
    }
}
