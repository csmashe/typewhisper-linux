using System.Diagnostics;

namespace TypeWhisper.Linux.Services;

public enum InsertionResult
{
    Pasted,
    CopiedToClipboard,
    NoText,
    ActionHandled,
    Failed,
}

/// <summary>
/// Text insertion on Linux. Default path on X11: xdotool types directly into
/// the focused window. On Wayland, xdotool will only work under XWayland —
/// native Wayland requires a compositor portal that's not universally available.
///
/// autoPaste=false copies to the clipboard via xclip (X11) or wl-copy (Wayland)
/// for the user to paste manually.
/// </summary>
public sealed class TextInsertionService
{
    public async Task<InsertionResult> InsertTextAsync(
        string text,
        bool autoPaste = true,
        string? targetWindowId = null)
    {
        if (string.IsNullOrEmpty(text))
            return InsertionResult.NoText;

        if (autoPaste)
        {
            return await TryXdotoolTypeAsync(text, targetWindowId);
        }

        return await TryCopyToClipboardAsync(text);
    }

    public async Task<string> CaptureSelectedTextAsync()
    {
        var previousClipboard = await TryReadClipboardAsync();

        if (!await TrySendCopyShortcutAsync())
            return "";

        await Task.Delay(150);
        var selectedText = await TryReadClipboardAsync() ?? "";

        if (previousClipboard is not null)
            await TryWriteClipboardTextAsync(previousClipboard);

        return selectedText;
    }

    private static async Task<InsertionResult> TryXdotoolTypeAsync(string text, string? targetWindowId)
    {
        try
        {
            var psi = new ProcessStartInfo("xdotool")
            {
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            if (!string.IsNullOrWhiteSpace(targetWindowId))
            {
                psi.ArgumentList.Add("windowactivate");
                psi.ArgumentList.Add("--sync");
                psi.ArgumentList.Add(targetWindowId);
                psi.ArgumentList.Add("type");
            }
            else
            {
                psi.ArgumentList.Add("type");
            }
            psi.ArgumentList.Add("--clearmodifiers");
            psi.ArgumentList.Add("--delay");
            psi.ArgumentList.Add("5");
            psi.ArgumentList.Add("--");
            psi.ArgumentList.Add(text);

            // Let the hotkey-release and overlay UI settle before re-focusing
            // the original target and typing into it.
            await Task.Delay(80);
            using var p = Process.Start(psi);
            if (p is null) return InsertionResult.Failed;
            await p.WaitForExitAsync();
            return p.ExitCode == 0 ? InsertionResult.Pasted : InsertionResult.Failed;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[TextInsertionService] xdotool failed: {ex.Message}");
            return InsertionResult.Failed;
        }
    }

    private static async Task<InsertionResult> TryCopyToClipboardAsync(string text)
    {
        try
        {
            return await TryWriteClipboardTextAsync(text)
                ? InsertionResult.CopiedToClipboard
                : InsertionResult.Failed;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[TextInsertionService] clipboard failed: {ex.Message}");
            return InsertionResult.Failed;
        }
    }

    private static async Task<bool> TrySendCopyShortcutAsync()
    {
        try
        {
            var psi = new ProcessStartInfo("xdotool")
            {
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            psi.ArgumentList.Add("key");
            psi.ArgumentList.Add("--clearmodifiers");
            psi.ArgumentList.Add("ctrl+c");

            using var p = Process.Start(psi);
            if (p is null) return false;
            await p.WaitForExitAsync();
            return p.ExitCode == 0;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[TextInsertionService] copy shortcut failed: {ex.Message}");
            return false;
        }
    }

    private static async Task<string?> TryReadClipboardAsync()
    {
        var isWayland = Environment.GetEnvironmentVariable("WAYLAND_DISPLAY") is { Length: > 0 };
        var psi = isWayland
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
            Debug.WriteLine($"[TextInsertionService] clipboard read failed: {ex.Message}");
            return null;
        }
    }

    private static async Task<bool> TryWriteClipboardTextAsync(string text)
    {
        var isWayland = Environment.GetEnvironmentVariable("WAYLAND_DISPLAY") is { Length: > 0 };
        var psi = isWayland
            ? new ProcessStartInfo("wl-copy")
            : new ProcessStartInfo("xclip", "-selection clipboard");
        psi.RedirectStandardInput = true;
        psi.RedirectStandardError = true;
        psi.UseShellExecute = false;

        using var p = Process.Start(psi);
        if (p is null) return false;
        await p.StandardInput.WriteAsync(text);
        p.StandardInput.Close();
        await p.WaitForExitAsync();
        return p.ExitCode == 0;
    }
}
