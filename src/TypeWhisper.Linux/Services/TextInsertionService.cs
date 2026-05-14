using System.Diagnostics;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;
using TypeWhisper.Linux.Services.Insertion;

namespace TypeWhisper.Linux.Services;

public enum InsertionResult
{
    Pasted,
    Typed,
    CopiedToClipboard,
    NoText,
    ActionHandled,
    ActionFailed,
    MissingClipboardTool,
    MissingPasteTool,
    Failed,
}

/// <summary>
/// Why the insertion fell back from direct paste/type to the clipboard.
/// Drives the wording of the fallback popup so we can tell the user
/// "set up ydotool" instead of the generic "paste with Ctrl+V".
/// </summary>
public enum InsertionFailureReason
{
    None,
    WtypeCompositorUnsupported,
    YdotoolSocketUnreachable,
    NoWaylandTypingTool,
    FocusFailed,
    PasteRetriesExhausted,
}

public sealed record TextInsertionRequest(
    string Text,
    bool AutoPaste = true,
    string? TargetWindowId = null,
    string? TargetProcessName = null,
    string? TargetWindowTitle = null,
    bool AutoEnter = false,
    TextInsertionStrategy Strategy = TextInsertionStrategy.Auto);

/// <summary>
/// Text insertion on Linux. The dispatch logic is a per-compositor
/// ordered backend chain: on GNOME / KDE Wayland we prefer ydotool
/// (since their compositors omit the wtype protocol), on wlroots
/// derivatives wtype keeps its first-tried slot. Every backend attempt
/// updates <see cref="LastFailureReason"/> so the orchestrator can
/// surface a setup hint instead of the generic "paste manually" popup
/// when fallback is the result of a known, fixable misconfiguration.
/// </summary>
public sealed class TextInsertionService
{
    private static readonly TimeSpan FocusDelay = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan ClipboardRestoreDelayDefault = TimeSpan.FromMilliseconds(200);
    // KDE Plasma's Klipper races us when restoring the clipboard — the
    // ~600 ms delay matches what OpenWhispr landed after the same race.
    private static readonly TimeSpan ClipboardRestoreDelayKde = TimeSpan.FromMilliseconds(600);
    private static readonly TimeSpan PasteRetryDelay = TimeSpan.FromMilliseconds(75);
    private const int PasteAttemptCount = 3;

    private readonly ITextInsertionPlatform _platform;
    private readonly IErrorLogService? _errorLog;

    public TextInsertionService()
        : this(new LinuxTextInsertionPlatform(), null)
    {
    }

    public TextInsertionService(IErrorLogService errorLog)
        : this(new LinuxTextInsertionPlatform(), errorLog)
    {
    }

    // DI-preferred ctor: takes the shared SystemCommandAvailabilityService
    // singleton so the platform can subscribe to snapshot refreshes
    // fired by YdotoolSetupHelper and rebuild its backend chain in
    // place — without this the singleton's chain is frozen at startup
    // and one-click ydotool setup appears to work but auto-paste keeps
    // falling back until the app is restarted.
    public TextInsertionService(IErrorLogService errorLog, SystemCommandAvailabilityService commands)
        : this(new LinuxTextInsertionPlatform(commands), errorLog)
    {
    }

    internal TextInsertionService(ITextInsertionPlatform platform, IErrorLogService? errorLog = null)
    {
        _platform = platform;
        _errorLog = errorLog;
    }

    /// <summary>
    /// Reason the most recent insertion fell back to the clipboard, or
    /// <see cref="InsertionFailureReason.None"/> after a successful
    /// paste/type. Read by <c>DictationOrchestrator</c> immediately
    /// after each <see cref="InsertTextAsync(TextInsertionRequest)"/>
    /// so the value is single-consumer in practice.
    /// </summary>
    public InsertionFailureReason LastFailureReason { get; private set; } = InsertionFailureReason.None;

    public async Task<InsertionResult> InsertTextAsync(
        string text,
        bool autoPaste = true,
        string? targetWindowId = null,
        string? targetProcessName = null,
        string? targetWindowTitle = null,
        bool autoEnter = false,
        TextInsertionStrategy strategy = TextInsertionStrategy.Auto) =>
        await InsertTextAsync(new TextInsertionRequest(
            text,
            autoPaste,
            targetWindowId,
            targetProcessName,
            targetWindowTitle,
            autoEnter,
            strategy));

    public async Task<InsertionResult> InsertTextAsync(TextInsertionRequest request)
    {
        LastFailureReason = InsertionFailureReason.None;

        var text = request.Text;
        var autoPaste = request.AutoPaste;
        var targetWindowId = request.TargetWindowId;
        var targetProcessName = request.TargetProcessName;
        var targetWindowTitle = request.TargetWindowTitle;
        var autoEnter = request.AutoEnter;
        var strategy = request.Strategy;

        if (string.IsNullOrEmpty(text))
            return autoEnter
                ? await SendEnterOnlyAsync(targetWindowId)
                : InsertionResult.NoText;

        if (strategy is TextInsertionStrategy.CopyOnly)
            autoPaste = false;

        if (autoPaste && !_platform.IsPasteAvailable)
        {
            LastFailureReason = InsertionFailureReason.NoWaylandTypingTool;
            return InsertionResult.MissingPasteTool;
        }

        var shouldTypeDirectly = autoPaste && strategy switch
        {
            TextInsertionStrategy.DirectTyping => true,
            TextInsertionStrategy.ClipboardPaste => false,
            _ => ShouldTypeDirectly(targetProcessName, targetWindowTitle)
                 // Wayland without xdotool can't identify the focused app,
                 // so process/title are both null. Defaulting to paste in
                 // that case hits things that don't bind Ctrl+V to paste
                 // (terminals, Claude Code's image-paste shortcut, vim
                 // normal mode). Direct typing via ydotool is universal —
                 // BUT only for ASCII. ydotool's `type` synthesizes evdev
                 // keycodes through the user's keyboard layout, so
                 // non-ASCII chars (smart quotes, em-dashes, accented
                 // letters, emoji) can silently render as the wrong glyph
                 // on non-US layouts. Fall back to clipboard paste when
                 // any non-ASCII byte is in the text — safer for unicode
                 // even if the resulting paste fails in a terminal, since
                 // the orchestrator's reason-aware fallback popup at
                 // least surfaces the issue instead of silently
                 // corrupting the user's document.
                 || (string.IsNullOrEmpty(targetProcessName)
                     && string.IsNullOrEmpty(targetWindowTitle)
                     && _platform.PrefersDirectTypingForUnknownTarget
                     && IsAsciiSafe(text))
        };

        if (shouldTypeDirectly)
        {
            var directResult = await TypeTextAsync(text, targetWindowId, autoEnter);
            if (strategy is TextInsertionStrategy.DirectTyping || directResult is not InsertionResult.Failed)
                return directResult;
        }

        if (!_platform.IsClipboardSetAvailable)
            return InsertionResult.MissingClipboardTool;

        var previousClipboard = await _platform.TryGetClipboardTextAsync();
        if (!await _platform.SetClipboardTextAsync(text))
            return InsertionResult.Failed;

        if (!autoPaste)
            return InsertionResult.CopiedToClipboard;

        if (!await FocusTargetWindowAsync(targetWindowId))
        {
            LastFailureReason = InsertionFailureReason.FocusFailed;
            LogInsertionFallback("Auto paste fell back to clipboard: target window could not be focused.");
            return InsertionResult.CopiedToClipboard;
        }

        if (!await TrySendPasteAsync())
        {
            // Prefer the platform's diagnostic (e.g. "compositor unsupported")
            // over the generic retries-exhausted reason.
            if (LastFailureReason == InsertionFailureReason.None)
                LastFailureReason = InsertionFailureReason.PasteRetriesExhausted;
            LogInsertionFallback("Auto paste fell back to clipboard: Ctrl+V could not be sent after retries.");
            return InsertionResult.CopiedToClipboard;
        }

        if (autoEnter && !await _platform.SendEnterAsync())
            LogInsertionFallback("Auto paste sent Ctrl+V, but Enter could not be sent.");

        await RestorePreviousClipboardAsync(previousClipboard);
        return InsertionResult.Pasted;
    }

    public async Task<string> CaptureSelectedTextAsync()
    {
        var previousClipboard = await _platform.TryGetClipboardTextAsync();

        if (!await _platform.SendCopyAsync())
            return "";

        await _platform.DelayAsync(TimeSpan.FromMilliseconds(150));
        var selectedText = await _platform.TryGetClipboardTextAsync() ?? "";

        if (previousClipboard is not null)
            await _platform.SetClipboardTextAsync(previousClipboard);

        return selectedText;
    }

    private async Task<bool> FocusTargetWindowAsync(string? targetWindowId)
    {
        if (string.IsNullOrWhiteSpace(targetWindowId))
        {
            await _platform.DelayAsync(FocusDelay);
            return true;
        }

        if (_platform.GetActiveWindowId() == targetWindowId)
        {
            await _platform.DelayAsync(FocusDelay);
            return true;
        }

        var focusRequested = await _platform.ActivateWindowAsync(targetWindowId);
        await _platform.DelayAsync(FocusDelay);
        return focusRequested || _platform.GetActiveWindowId() == targetWindowId;
    }

    private async Task RestorePreviousClipboardAsync(string? previousClipboard)
    {
        var delay = _platform.IsKdePlasma ? ClipboardRestoreDelayKde : ClipboardRestoreDelayDefault;
        await _platform.DelayAsync(delay);
        if (previousClipboard is null)
            return;

        try
        {
            await _platform.SetClipboardTextAsync(previousClipboard);
        }
        catch
        {
            // Best effort restore.
        }
    }

    private async Task<bool> TrySendPasteAsync()
    {
        for (var attempt = 1; attempt <= PasteAttemptCount; attempt++)
        {
            if (await _platform.SendPasteAsync())
                return true;

            // If the platform identified a structural reason on the
            // first attempt (compositor unsupported, socket missing),
            // retrying won't help — let the caller's reason-aware popup
            // take over immediately.
            var platformReason = _platform.LastFailureReason;
            if (platformReason is InsertionFailureReason.WtypeCompositorUnsupported
                or InsertionFailureReason.YdotoolSocketUnreachable
                or InsertionFailureReason.NoWaylandTypingTool)
            {
                LastFailureReason = platformReason;
                return false;
            }

            if (attempt < PasteAttemptCount)
                await _platform.DelayAsync(PasteRetryDelay);
        }

        return false;
    }

    private async Task<InsertionResult> TypeTextAsync(string text, string? targetWindowId, bool autoEnter)
    {
        if (!await FocusTargetWindowAsync(targetWindowId))
        {
            LastFailureReason = InsertionFailureReason.FocusFailed;
            LogInsertionFallback("Direct typing fell back: target window could not be focused.");
            return InsertionResult.Failed;
        }

        if (!await _platform.TypeTextAsync(text))
        {
            if (_platform.LastFailureReason != InsertionFailureReason.None)
                LastFailureReason = _platform.LastFailureReason;
            LogInsertionFallback("Direct typing failed.");
            return InsertionResult.Failed;
        }

        if (autoEnter && !await _platform.SendEnterAsync())
            LogInsertionFallback("Direct typing succeeded, but Enter could not be sent.");

        return InsertionResult.Typed;
    }

    private async Task<InsertionResult> SendEnterOnlyAsync(string? targetWindowId)
    {
        if (!_platform.IsPasteAvailable)
            return InsertionResult.MissingPasteTool;

        if (!await FocusTargetWindowAsync(targetWindowId))
        {
            LogInsertionFallback("Enter command failed: target window could not be focused.");
            return InsertionResult.ActionFailed;
        }

        return await _platform.SendEnterAsync()
            ? InsertionResult.ActionHandled
            : InsertionResult.ActionFailed;
    }

    private static bool ShouldTypeDirectly(string? processName, string? windowTitle)
    {
        return ContainsCodex(processName)
            || ContainsCodex(windowTitle)
            || ShouldTypeBrowserDirectly(processName, windowTitle)
            || IsTerminalProcess(processName);

        static bool ContainsCodex(string? value) =>
            !string.IsNullOrWhiteSpace(value)
            && value.Contains("codex", StringComparison.OrdinalIgnoreCase);

        static bool ShouldTypeBrowserDirectly(string? processName, string? title) =>
            ActiveWindowService.IsSupportedBrowserWindow(processName, title)
            && !IsMailBrowserWindow(title);

        static bool IsMailBrowserWindow(string? title) =>
            !string.IsNullOrWhiteSpace(title)
            && (title.Contains(" Mail", StringComparison.OrdinalIgnoreCase)
                || title.Contains("Gmail", StringComparison.OrdinalIgnoreCase));

        // Terminals don't interpret a synthesized Ctrl+V as paste — bash's
        // readline binds it to quoted-insert, so the paste produces nothing
        // visible. Direct typing avoids the modifier entirely. The list
        // covers known terminals by exact name; the trailing pattern check
        // catches the long tail (anything that ends with "term" or
        // "-terminal", e.g. xfce4-terminal, deepin-terminal, lxterm) so a
        // new terminal doesn't require a code change to be supported.
        static bool IsTerminalProcess(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return false;

            var process = ProcessNameNormalizer.Normalize(value);
            if (process.Equals("kitty", StringComparison.OrdinalIgnoreCase)
                || process.Equals("gnome-terminal", StringComparison.OrdinalIgnoreCase)
                || process.Equals("konsole", StringComparison.OrdinalIgnoreCase)
                || process.Equals("alacritty", StringComparison.OrdinalIgnoreCase)
                || process.Equals("wezterm", StringComparison.OrdinalIgnoreCase)
                || process.Equals("xterm", StringComparison.OrdinalIgnoreCase)
                || process.Equals("tilix", StringComparison.OrdinalIgnoreCase)
                || process.Equals("ghostty", StringComparison.OrdinalIgnoreCase)
                || process.Equals("foot", StringComparison.OrdinalIgnoreCase)
                || process.Equals("ptyxis", StringComparison.OrdinalIgnoreCase)
                || process.Equals("terminator", StringComparison.OrdinalIgnoreCase)
                || process.Equals("warp", StringComparison.OrdinalIgnoreCase)
                || process.Equals("hyper", StringComparison.OrdinalIgnoreCase)
                || process.Equals("st", StringComparison.OrdinalIgnoreCase)
                || process.Equals("urxvt", StringComparison.OrdinalIgnoreCase)
                || process.Equals("rxvt", StringComparison.OrdinalIgnoreCase))
                return true;

            return process.EndsWith("-terminal", StringComparison.OrdinalIgnoreCase)
                || process.EndsWith("term", StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// Pure-ASCII safety check used to decide whether ydotool's
    /// layout-dependent <c>type</c> can be trusted for an unknown target.
    /// 0x09 (tab), 0x0A (newline), and 0x20–0x7E (printable ASCII) are
    /// the keycodes ydotool can synthesize without consulting a non-US
    /// layout; everything else may render as a wrong glyph and is
    /// routed through clipboard paste instead.
    /// </summary>
    private static bool IsAsciiSafe(string text)
    {
        foreach (var c in text)
        {
            if (c is '\t' or '\n' or '\r')
                continue;
            if (c < 0x20 || c > 0x7E)
                return false;
        }
        return true;
    }

    private void LogInsertionFallback(string message)
    {
        Trace.WriteLine($"[TextInsertionService] {message}");
        try
        {
            _errorLog?.AddEntry(message, ErrorCategory.Insertion);
        }
        catch
        {
            // Diagnostics must never block dictation output.
        }
    }
}

internal interface ITextInsertionPlatform
{
    bool IsClipboardSetAvailable { get; }
    bool IsPasteAvailable { get; }
    bool IsKdePlasma { get; }
    /// <summary>
    /// True when the platform should default to direct typing for any
    /// target it cannot identify. On Wayland-without-xdotool we have no
    /// reliable active-window detection, so <c>targetProcessName</c> is
    /// almost always null; the paste path then sends a Ctrl+V that
    /// terminals reject (readline quoted-insert), Claude Code interprets
    /// as image paste, vim sees as normal-mode garbage, etc. Direct
    /// typing via ydotool works in all of those.
    /// </summary>
    bool PrefersDirectTypingForUnknownTarget { get; }
    InsertionFailureReason LastFailureReason { get; }
    Task<string?> TryGetClipboardTextAsync();
    Task<bool> SetClipboardTextAsync(string text);
    Task DelayAsync(TimeSpan delay);
    string? GetActiveWindowId();
    Task<bool> ActivateWindowAsync(string windowId);
    Task<bool> SendPasteAsync();
    Task<bool> TypeTextAsync(string text);
    Task<bool> SendCopyAsync();
    Task<bool> SendEnterAsync();
}

/// <summary>
/// Wire-level adapter that walks a per-compositor backend chain. The
/// chain is built once at construction; per-attempt failure reasons are
/// surfaced through <see cref="LastFailureReason"/> so the higher layer
/// can stop retrying when the failure is structural (compositor refused
/// wtype, ydotool socket missing) rather than transient.
/// </summary>
internal sealed class LinuxTextInsertionPlatform : ITextInsertionPlatform
{
    internal enum InputBackend
    {
        None,
        Xdotool,
        Wtype,
        Ydotool,
    }

    internal delegate Task<int> ProcessRunnerWithEnv(
        string fileName,
        IReadOnlyList<string> args,
        IReadOnlyDictionary<string, string>? env);

    private LinuxCapabilitySnapshot _snapshot;
    private readonly ProcessRunnerWithEnv _processRunner;
    private readonly Func<string, IReadOnlyList<string>, Task<(int exitCode, string stderr)>>? _processRunnerWithStderr;
    private IReadOnlyList<InputBackend> _chain;
    private readonly bool _isWayland;
    private readonly HashSet<InputBackend> _disabled = new();
    private InsertionFailureReason _lastFailureReason = InsertionFailureReason.None;
    private readonly SystemCommandAvailabilityService? _commands;

    public LinuxTextInsertionPlatform()
        : this(
            new SystemCommandAvailabilityService(),
            DefaultProcessRunnerWithEnv,
            DefaultProcessRunnerWithStderr)
    {
    }

    public LinuxTextInsertionPlatform(SystemCommandAvailabilityService commands)
        : this(commands, DefaultProcessRunnerWithEnv, DefaultProcessRunnerWithStderr)
    {
    }

    internal LinuxTextInsertionPlatform(
        SystemCommandAvailabilityService commands,
        ProcessRunnerWithEnv processRunner,
        Func<string, IReadOnlyList<string>, Task<(int exitCode, string stderr)>>? processRunnerWithStderr)
        : this(commands.GetSnapshot(), processRunner, processRunnerWithStderr)
    {
        _commands = commands;
        // Subscribe so that when YdotoolSetupHelper.SetUpAsync (or any
        // future code path) calls RefreshSnapshot the live chain
        // rebuilds in place. Without this the DI singleton freezes its
        // chain at startup and the one-click ydotool setup looks
        // successful but auto-paste still falls through until restart.
        commands.SnapshotChanged += OnSnapshotChanged;
    }

    internal LinuxTextInsertionPlatform(
        LinuxCapabilitySnapshot snapshot,
        Func<string, IReadOnlyList<string>, Task<int>> processRunner)
        : this(
            snapshot,
            (file, args, env) => processRunner(file, args),
            // Tests inject the legacy single-return runner that already
            // records wtype's invocation but doesn't surface stderr.
            // Adapt it to the stderr-aware shape so the chain stays on a
            // single mocked code path — otherwise real wtype would be
            // spawned, fail, and the test would never see its argv.
            async (file, args) => (await processRunner(file, args).ConfigureAwait(false), string.Empty))
    {
    }

    internal LinuxTextInsertionPlatform(
        LinuxCapabilitySnapshot snapshot,
        ProcessRunnerWithEnv processRunner,
        Func<string, IReadOnlyList<string>, Task<(int exitCode, string stderr)>>? processRunnerWithStderr = null)
    {
        _snapshot = snapshot;
        _processRunner = processRunner;
        _processRunnerWithStderr = processRunnerWithStderr;
        _isWayland = snapshot.SessionType == "Wayland";
        _chain = BuildChain(snapshot);
    }

    private void OnSnapshotChanged(object? sender, LinuxCapabilitySnapshot snapshot)
        => ApplyRefreshedSnapshot(snapshot);

    /// <summary>
    /// Re-reads the capability snapshot and rebuilds the backend chain
    /// in place. Called from the SnapshotChanged subscription so that
    /// the live singleton picks up newly-installed tools (ydotool
    /// daemon, wtype, etc.) without an app restart.
    /// </summary>
    internal void ApplyRefreshedSnapshot(LinuxCapabilitySnapshot snapshot)
    {
        _snapshot = snapshot;
        _chain = BuildChain(snapshot);
        // Clear sticky disables: e.g. wtype was marked unsupported on
        // last try, but the user has now switched compositors or
        // installed ydotool — give every backend another shot.
        _disabled.Clear();
        _lastFailureReason = InsertionFailureReason.None;
    }

    public bool IsClipboardSetAvailable => _isWayland
        ? IsCommandAvailable("wl-copy")
        : IsCommandAvailable("xclip");

    public bool IsPasteAvailable => _chain.Count > 0;

    public bool IsKdePlasma => _snapshot.Compositor == "kde";

    public bool PrefersDirectTypingForUnknownTarget => _isWayland && !_snapshot.HasXdotool;

    public InsertionFailureReason LastFailureReason => _lastFailureReason;

    public async Task<string?> TryGetClipboardTextAsync()
    {
        var psi = _isWayland
            ? new ProcessStartInfo("wl-paste", "--no-newline")
            : new ProcessStartInfo("xclip", "-selection clipboard -o");
        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError = true;
        psi.UseShellExecute = false;

        try
        {
            using var p = Process.Start(psi);
            if (p is null) return null;
            var output = await p.StandardOutput.ReadToEndAsync();
            await p.WaitForExitAsync();
            return p.ExitCode == 0 ? output : null;
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[TextInsertionService] clipboard read failed: {ex.Message}");
            return null;
        }
    }

    public async Task<bool> SetClipboardTextAsync(string text)
    {
        var psi = _isWayland
            ? new ProcessStartInfo("wl-copy")
            : new ProcessStartInfo("xclip", "-selection clipboard");
        psi.RedirectStandardInput = true;
        psi.RedirectStandardError = true;
        psi.UseShellExecute = false;

        try
        {
            using var p = Process.Start(psi);
            if (p is null) return false;
            await p.StandardInput.WriteAsync(text);
            p.StandardInput.Close();
            await p.WaitForExitAsync();
            return p.ExitCode == 0;
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[TextInsertionService] clipboard write failed: {ex.Message}");
            return false;
        }
    }

    public Task DelayAsync(TimeSpan delay) => Task.Delay(delay);

    public string? GetActiveWindowId()
    {
        // xdotool's getactivewindow returns the X server's idea of the
        // focused window, which on Wayland is the XWayland surface (if
        // any) — useless for ydotool/wtype. Only X11 sessions get a
        // meaningful window id.
        if (_isWayland || !_snapshot.HasXdotool)
            return null;

        var output = RunXdotoolSync("getactivewindow");
        return string.IsNullOrWhiteSpace(output) ? null : output;
    }

    public async Task<bool> ActivateWindowAsync(string windowId)
    {
        // On Wayland we can't focus a window from the client side; the
        // overlay-restore plumbing relies on the compositor having
        // already restored focus by the time we get here.
        if (_isWayland)
            return true;

        if (!_snapshot.HasXdotool)
            return false;

        return await RunWithEnv("xdotool", new[] { "windowactivate", "--sync", windowId }, null) == 0;
    }

    public async Task<bool> SendPasteAsync() => await WalkChainAsync(
        async backend => backend switch
        {
            InputBackend.Wtype => await RunWtypeAsync("-M", "ctrl", "v", "-m", "ctrl"),
            InputBackend.Xdotool => await SendModifiedKeyAsync("Control_L", "v"),
            InputBackend.Ydotool => await RunYdotoolAsync(YdotoolBackend.PasteArgs()),
            _ => false,
        });

    public async Task<bool> TypeTextAsync(string text) => await WalkChainAsync(
        async backend => backend switch
        {
            InputBackend.Wtype => await RunWtypeAsync("--", text),
            InputBackend.Xdotool => await RunWithEnv("xdotool", new[] { "type", "--clearmodifiers", "--delay", "8", "--", text }, null) == 0,
            InputBackend.Ydotool => await RunYdotoolAsync(YdotoolBackend.TypeArgs(text)),
            _ => false,
        });

    public async Task<bool> SendCopyAsync() => await WalkChainAsync(
        async backend => backend switch
        {
            InputBackend.Wtype => await RunWtypeAsync("-M", "ctrl", "c", "-m", "ctrl"),
            InputBackend.Xdotool => await SendModifiedKeyAsync("Control_L", "c"),
            InputBackend.Ydotool => await RunYdotoolAsync(YdotoolBackend.CopyArgs()),
            _ => false,
        });

    public async Task<bool> SendEnterAsync() => await WalkChainAsync(
        async backend => backend switch
        {
            InputBackend.Wtype => await RunWtypeAsync("-k", "Return"),
            InputBackend.Xdotool => await RunWithEnv("xdotool", new[] { "key", "--clearmodifiers", "Return" }, null) == 0,
            InputBackend.Ydotool => await RunYdotoolAsync(YdotoolBackend.EnterArgs()),
            _ => false,
        });

    private async Task<bool> WalkChainAsync(Func<InputBackend, Task<bool>> attempt)
    {
        _lastFailureReason = InsertionFailureReason.None;
        if (_chain.Count == 0)
        {
            _lastFailureReason = InsertionFailureReason.NoWaylandTypingTool;
            return false;
        }

        var anyAttempted = false;
        foreach (var backend in _chain)
        {
            if (_disabled.Contains(backend))
                continue;
            anyAttempted = true;
            if (await attempt(backend))
                return true;
        }

        if (!anyAttempted)
            _lastFailureReason = InsertionFailureReason.NoWaylandTypingTool;
        return false;
    }

    /// <summary>
    /// Build the ordered list of backends to try. Ordering is the heart
    /// of this phase: GNOME / KDE Wayland get ydotool first because
    /// wtype is doomed there; wlroots compositors (Hyprland / Sway /
    /// unknown wlroots-shaped sessions) keep wtype as the canonical
    /// fast path; X11 stays xdotool-only.
    /// </summary>
    private static IReadOnlyList<InputBackend> BuildChain(LinuxCapabilitySnapshot snapshot)
    {
        var chain = new List<InputBackend>();

        if (snapshot.SessionType == "Wayland")
        {
            var ydotoolUsable = snapshot.HasYdotool && snapshot.HasYdotoolSocket;
            if (snapshot.CompositorRejectsWtype)
            {
                if (ydotoolUsable) chain.Add(InputBackend.Ydotool);
                if (snapshot.HasWtype) chain.Add(InputBackend.Wtype);
                if (snapshot.HasXdotool) chain.Add(InputBackend.Xdotool);
            }
            else
            {
                if (snapshot.HasWtype) chain.Add(InputBackend.Wtype);
                if (ydotoolUsable) chain.Add(InputBackend.Ydotool);
                if (snapshot.HasXdotool) chain.Add(InputBackend.Xdotool);
            }
        }
        else if (snapshot.HasXdotool)
        {
            chain.Add(InputBackend.Xdotool);
        }

        return chain;
    }

    private async Task<bool> SendModifiedKeyAsync(string modifier, string key)
    {
        var keyDown = await RunWithEnv("xdotool", new[] { "keydown", "--clearmodifiers", modifier }, null) == 0;
        var keySent = false;
        try
        {
            if (keyDown)
                keySent = await RunWithEnv("xdotool", new[] { "key", key }, null) == 0;
        }
        finally
        {
            await RunWithEnv("xdotool", new[] { "keyup", modifier }, null);
        }

        return keyDown && keySent;
    }

    private static bool IsCommandAvailable(string command)
    {
        return SystemCommandAvailabilityService.IsCommandAvailable(command);
    }

    private static string? RunXdotoolSync(string arguments)
    {
        try
        {
            var psi = new ProcessStartInfo("xdotool", arguments)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            using var p = Process.Start(psi);
            if (p is null) return null;

            var stdoutTask = p.StandardOutput.ReadToEndAsync();
            var stderrTask = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(1000))
            {
                try { p.Kill(entireProcessTree: true); } catch { /* best effort */ }
                return null;
            }

            var output = stdoutTask.GetAwaiter().GetResult();
            stderrTask.GetAwaiter().GetResult();
            return p.ExitCode == 0 ? output.Trim() : null;
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[TextInsertionService] xdotool failed: {ex.Message}");
            return null;
        }
    }

    private async Task<bool> RunWtypeAsync(params string[] args)
    {
        // Capture stderr so we can detect "Compositor does not support
        // the virtual keyboard protocol" and skip wtype permanently for
        // the rest of this process — without that fast-skip, every
        // dictation on GNOME / KDE Wayland wastes ~225 ms retrying a
        // doomed backend before falling through.
        if (_processRunnerWithStderr is not null)
        {
            var (exitCode, stderr) = await _processRunnerWithStderr("wtype", args).ConfigureAwait(false);
            if (exitCode != 0 && IsWtypeCompositorRejection(stderr))
            {
                _disabled.Add(InputBackend.Wtype);
                // First-failing backend's reason wins: if an earlier
                // backend (e.g. ydotool) already recorded a specific
                // diagnostic, keep it. Otherwise on GNOME/KDE with a
                // broken-but-set-up ydotool the user would see "Set up
                // ydotool" advice when ydotool is the actual problem.
                if (_lastFailureReason == InsertionFailureReason.None)
                    _lastFailureReason = InsertionFailureReason.WtypeCompositorUnsupported;
            }
            return exitCode == 0;
        }

        return await RunWithEnv("wtype", args, null) == 0;
    }

    private static bool IsWtypeCompositorRejection(string stderr) =>
        !string.IsNullOrEmpty(stderr)
        && (stderr.Contains("Compositor does not support", StringComparison.OrdinalIgnoreCase)
            || stderr.Contains("virtual keyboard", StringComparison.OrdinalIgnoreCase));

    private async Task<bool> RunYdotoolAsync(IReadOnlyList<string> args)
    {
        var env = YdotoolBackend.BuildEnv(_snapshot.YdotoolSocketPath);
        if (env is null)
        {
            if (_lastFailureReason == InsertionFailureReason.None)
                _lastFailureReason = InsertionFailureReason.YdotoolSocketUnreachable;
            _disabled.Add(InputBackend.Ydotool);
            return false;
        }
        var exit = await RunWithEnv(YdotoolBackend.ExecutableName, args, env);
        if (exit != 0)
        {
            // Daemon ran but the request failed — almost always EACCES
            // on /dev/uinput (user not in input group, uaccess didn't
            // apply) or a wedged socket. Either way the failure is
            // sticky for this process lifetime: disable so the chain
            // skips it next time instead of paying for another spawn,
            // and surface a ydotool-specific reason so the orchestrator
            // can tell the user to check the Text insertion panel.
            if (_lastFailureReason == InsertionFailureReason.None)
                _lastFailureReason = InsertionFailureReason.YdotoolSocketUnreachable;
            _disabled.Add(InputBackend.Ydotool);
            return false;
        }
        return true;
    }

    private Task<int> RunWithEnv(
        string fileName,
        IReadOnlyList<string> args,
        IReadOnlyDictionary<string, string>? env) =>
        _processRunner(fileName, args, env);

    private static async Task<int> DefaultProcessRunnerWithEnv(
        string fileName,
        IReadOnlyList<string> args,
        IReadOnlyDictionary<string, string>? env)
    {
        try
        {
            var psi = new ProcessStartInfo(fileName)
            {
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            foreach (var arg in args)
                psi.ArgumentList.Add(arg);
            if (env is not null)
            {
                foreach (var (key, value) in env)
                    psi.Environment[key] = value;
            }

            using var p = Process.Start(psi);
            if (p is null) return -1;
            await p.WaitForExitAsync();
            return p.ExitCode;
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[TextInsertionService] {fileName} failed: {ex.Message}");
            return -1;
        }
    }

    private static async Task<(int exitCode, string stderr)> DefaultProcessRunnerWithStderr(
        string fileName,
        IReadOnlyList<string> args)
    {
        try
        {
            var psi = new ProcessStartInfo(fileName)
            {
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
            };
            foreach (var arg in args)
                psi.ArgumentList.Add(arg);

            using var p = Process.Start(psi);
            if (p is null) return (-1, string.Empty);
            var stderrTask = p.StandardError.ReadToEndAsync();
            var stdoutTask = p.StandardOutput.ReadToEndAsync();
            await p.WaitForExitAsync();
            var stderr = await stderrTask.ConfigureAwait(false);
            await stdoutTask.ConfigureAwait(false);
            return (p.ExitCode, stderr);
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[TextInsertionService] {fileName} failed: {ex.Message}");
            return (-1, string.Empty);
        }
    }
}
