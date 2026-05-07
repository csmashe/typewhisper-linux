using System.Diagnostics;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;

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

public sealed record TextInsertionRequest(
    string Text,
    bool AutoPaste = true,
    string? TargetWindowId = null,
    string? TargetProcessName = null,
    string? TargetWindowTitle = null,
    bool AutoEnter = false,
    TextInsertionStrategy Strategy = TextInsertionStrategy.Auto);

/// <summary>
/// Text insertion on Linux. The reliable path is clipboard-first: put the
/// transcription on the clipboard, refocus the captured target window when
/// available, then ask the resolved input backend (wtype on Wayland, xdotool
/// on X11/XWayland) to paste. If focus or paste fails, the text is
/// intentionally left on the clipboard so the user can paste manually.
/// </summary>
public sealed class TextInsertionService
{
    private static readonly TimeSpan FocusDelay = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan ClipboardRestoreDelay = TimeSpan.FromMilliseconds(200);
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

    internal TextInsertionService(ITextInsertionPlatform platform, IErrorLogService? errorLog = null)
    {
        _platform = platform;
        _errorLog = errorLog;
    }

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
            return InsertionResult.MissingPasteTool;

        var shouldTypeDirectly = autoPaste && strategy switch
        {
            TextInsertionStrategy.DirectTyping => true,
            TextInsertionStrategy.ClipboardPaste => false,
            _ => ShouldTypeDirectly(targetProcessName, targetWindowTitle)
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
            LogInsertionFallback("Auto paste fell back to clipboard: target window could not be focused.");
            return InsertionResult.CopiedToClipboard;
        }

        if (!await TrySendPasteAsync())
        {
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
        await _platform.DelayAsync(ClipboardRestoreDelay);
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

            if (attempt < PasteAttemptCount)
                await _platform.DelayAsync(PasteRetryDelay);
        }

        return false;
    }

    private async Task<InsertionResult> TypeTextAsync(string text, string? targetWindowId, bool autoEnter)
    {
        if (!await FocusTargetWindowAsync(targetWindowId))
        {
            LogInsertionFallback("Direct typing fell back: target window could not be focused.");
            return InsertionResult.Failed;
        }

        if (!await _platform.TypeTextAsync(text))
        {
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

        static bool IsTerminalProcess(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return false;

            var process = ProcessNameNormalizer.Normalize(value);
            return process.Equals("kitty", StringComparison.OrdinalIgnoreCase)
                || process.Equals("gnome-terminal", StringComparison.OrdinalIgnoreCase)
                || process.Equals("konsole", StringComparison.OrdinalIgnoreCase)
                || process.Equals("alacritty", StringComparison.OrdinalIgnoreCase)
                || process.Equals("wezterm", StringComparison.OrdinalIgnoreCase)
                || process.Equals("xterm", StringComparison.OrdinalIgnoreCase)
                || process.Equals("tilix", StringComparison.OrdinalIgnoreCase);
        }
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

internal sealed class LinuxTextInsertionPlatform : ITextInsertionPlatform
{
    private enum InputBackend
    {
        None,
        Xdotool,
        Wtype,
    }

    private readonly LinuxCapabilitySnapshot _snapshot;
    private readonly Func<string, IReadOnlyList<string>, Task<int>> _processRunner;
    private readonly InputBackend _inputBackend;
    private readonly bool _isWayland;

    public LinuxTextInsertionPlatform()
        : this(new SystemCommandAvailabilityService().GetSnapshot(), DefaultProcessRunner)
    {
    }

    internal LinuxTextInsertionPlatform(
        LinuxCapabilitySnapshot snapshot,
        Func<string, IReadOnlyList<string>, Task<int>> processRunner)
    {
        _snapshot = snapshot;
        _processRunner = processRunner;
        _isWayland = snapshot.SessionType == "Wayland";
        _inputBackend = _isWayland && snapshot.HasWtype
            ? InputBackend.Wtype
            : snapshot.HasXdotool
                ? InputBackend.Xdotool
                : InputBackend.None;
    }

    public bool IsClipboardSetAvailable => _isWayland
        ? IsCommandAvailable("wl-copy")
        : IsCommandAvailable("xclip");

    public bool IsPasteAvailable => _inputBackend != InputBackend.None;

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
        if (_inputBackend != InputBackend.Xdotool)
            return null;

        var output = RunXdotool("getactivewindow");
        return string.IsNullOrWhiteSpace(output) ? null : output;
    }

    public async Task<bool> ActivateWindowAsync(string windowId)
    {
        if (_inputBackend == InputBackend.Wtype)
            return true;

        if (_inputBackend == InputBackend.None)
            return false;

        return await RunXdotoolAsync("windowactivate", "--sync", windowId) == 0;
    }

    public async Task<bool> SendPasteAsync() => _inputBackend switch
    {
        InputBackend.Wtype => await RunWtypeAsync("-M", "ctrl", "v", "-m", "ctrl") == 0,
        InputBackend.Xdotool => await SendModifiedKeyAsync("Control_L", "v"),
        _ => false,
    };

    public async Task<bool> TypeTextAsync(string text) => _inputBackend switch
    {
        InputBackend.Wtype => await RunWtypeAsync("--", text) == 0,
        InputBackend.Xdotool => await RunXdotoolAsync("type", "--clearmodifiers", "--delay", "8", "--", text) == 0,
        _ => false,
    };

    public async Task<bool> SendCopyAsync() => _inputBackend switch
    {
        InputBackend.Wtype => await RunWtypeAsync("-M", "ctrl", "c", "-m", "ctrl") == 0,
        InputBackend.Xdotool => await SendModifiedKeyAsync("Control_L", "c"),
        _ => false,
    };

    public async Task<bool> SendEnterAsync() => _inputBackend switch
    {
        InputBackend.Wtype => await RunWtypeAsync("-k", "Return") == 0,
        InputBackend.Xdotool => await RunXdotoolAsync("key", "--clearmodifiers", "Return") == 0,
        _ => false,
    };

    private async Task<bool> SendModifiedKeyAsync(string modifier, string key)
    {
        var keyDown = await RunXdotoolAsync("keydown", "--clearmodifiers", modifier) == 0;
        var keySent = false;
        try
        {
            if (keyDown)
                keySent = await RunXdotoolAsync("key", key) == 0;
        }
        finally
        {
            await RunXdotoolAsync("keyup", modifier);
        }

        return keyDown && keySent;
    }

    private static bool IsCommandAvailable(string command)
    {
        return SystemCommandAvailabilityService.IsCommandAvailable(command);
    }

    private static string? RunXdotool(string arguments)
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
            var output = p.StandardOutput.ReadToEnd();
            p.WaitForExit();
            return p.ExitCode == 0 ? output.Trim() : null;
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[TextInsertionService] xdotool failed: {ex.Message}");
            return null;
        }
    }

    private Task<int> RunXdotoolAsync(params string[] args) =>
        _processRunner("xdotool", args);

    private Task<int> RunWtypeAsync(params string[] args) =>
        _processRunner("wtype", args);

    private static async Task<int> DefaultProcessRunner(string fileName, IReadOnlyList<string> args)
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
}
