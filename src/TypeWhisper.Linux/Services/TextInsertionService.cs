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
    public async Task<InsertionResult> InsertTextAsync(string text, bool autoPaste = true)
    {
        if (string.IsNullOrEmpty(text))
            return InsertionResult.NoText;

        if (autoPaste)
        {
            // Let the hotkey-release UI settle before typing.
            await Task.Delay(80);
            return await TryXdotoolTypeAsync(text);
        }

        return await TryCopyToClipboardAsync(text);
    }

    private static async Task<InsertionResult> TryXdotoolTypeAsync(string text)
    {
        try
        {
            var psi = new ProcessStartInfo("xdotool")
            {
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            psi.ArgumentList.Add("type");
            psi.ArgumentList.Add("--clearmodifiers");
            psi.ArgumentList.Add("--delay");
            psi.ArgumentList.Add("5");
            psi.ArgumentList.Add("--");
            psi.ArgumentList.Add(text);

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
        var isWayland = Environment.GetEnvironmentVariable("WAYLAND_DISPLAY") is { Length: > 0 };
        var psi = isWayland
            ? new ProcessStartInfo("wl-copy")
            : new ProcessStartInfo("xclip", "-selection clipboard");
        psi.RedirectStandardInput = true;
        psi.RedirectStandardError = true;
        psi.UseShellExecute = false;

        try
        {
            using var p = Process.Start(psi);
            if (p is null) return InsertionResult.Failed;
            await p.StandardInput.WriteAsync(text);
            p.StandardInput.Close();
            await p.WaitForExitAsync();
            return p.ExitCode == 0 ? InsertionResult.CopiedToClipboard : InsertionResult.Failed;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[TextInsertionService] clipboard failed: {ex.Message}");
            return InsertionResult.Failed;
        }
    }
}
