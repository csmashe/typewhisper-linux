using System.Diagnostics;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;

namespace TypeWhisper.Linux.Services;

public enum InsertionResult
{
    Pasted,
    CopiedToClipboard,
    NoText,
    ActionHandled,
    ActionFailed,
    Failed,
}

/// <summary>
/// Text insertion on Linux. The reliable path is clipboard-first: put the
/// transcription on the clipboard, refocus the captured target window when
/// available, then ask xdotool to paste. If focus or paste fails, the text is
/// intentionally left on the clipboard so the user can paste manually.
/// </summary>
public sealed class TextInsertionService
{
    private static readonly TimeSpan FocusDelay = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan ClipboardRestoreDelay = TimeSpan.FromMilliseconds(200);

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
        bool autoEnter = false)
    {
        if (string.IsNullOrEmpty(text))
            return InsertionResult.NoText;

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

        if (!await _platform.SendPasteAsync())
        {
            LogInsertionFallback("Auto paste fell back to clipboard: Ctrl+V could not be sent.");
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
    Task<string?> TryGetClipboardTextAsync();
    Task<bool> SetClipboardTextAsync(string text);
    Task DelayAsync(TimeSpan delay);
    string? GetActiveWindowId();
    Task<bool> ActivateWindowAsync(string windowId);
    Task<bool> SendPasteAsync();
    Task<bool> SendCopyAsync();
    Task<bool> SendEnterAsync();
}

internal sealed class LinuxTextInsertionPlatform : ITextInsertionPlatform
{
    public async Task<string?> TryGetClipboardTextAsync()
    {
        var psi = IsWayland()
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
        var psi = IsWayland()
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
        var output = RunXdotool("getactivewindow");
        return string.IsNullOrWhiteSpace(output) ? null : output;
    }

    public async Task<bool> ActivateWindowAsync(string windowId) =>
        await RunXdotoolAsync("windowactivate", "--sync", windowId) == 0;

    public async Task<bool> SendPasteAsync() =>
        await RunXdotoolAsync("key", "--clearmodifiers", "ctrl+v") == 0;

    public async Task<bool> SendCopyAsync() =>
        await RunXdotoolAsync("key", "--clearmodifiers", "ctrl+c") == 0;

    public async Task<bool> SendEnterAsync() =>
        await RunXdotoolAsync("key", "--clearmodifiers", "Return") == 0;

    private static bool IsWayland() =>
        Environment.GetEnvironmentVariable("WAYLAND_DISPLAY") is { Length: > 0 };

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

    private static async Task<int> RunXdotoolAsync(params string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo("xdotool")
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
            Trace.WriteLine($"[TextInsertionService] xdotool failed: {ex.Message}");
            return -1;
        }
    }
}
