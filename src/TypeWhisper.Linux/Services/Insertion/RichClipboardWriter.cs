using System.Diagnostics;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Threading;

namespace TypeWhisper.Linux.Services.Insertion;

/// <summary>One clipboard target and the exact bytes its owner served for it.</summary>
public sealed record ClipboardFormatData(string MimeType, byte[] Data);

/// <summary>
///     Puts a multi-format clipboard back through Avalonia's X11 clipboard, which (unlike
///     xclip, which serves a single target) answers TARGETS with every format at once.
/// </summary>
internal static class RichClipboardWriter
{
    public static async Task<bool> TrySetAsync(string text, IReadOnlyList<ClipboardFormatData> formats)
    {
        if (Application.Current is null)
        {
            return false;
        }

        try
        {
            return await Dispatcher.UIThread.InvokeAsync(async () =>
            {
                if (GetClipboard() is not { } clipboard)
                {
                    return false;
                }

                var item = new DataTransferItem();
                item.SetText(text);
                foreach (var format in formats)
                {
                    item.Set(DataFormat.CreateBytesPlatformFormat(format.MimeType), format.Data);
                }

                var transfer = new DataTransfer();
                transfer.Add(item);
                await clipboard.SetDataAsync(transfer);
                return true;
            });
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[TextInsertionService] rich clipboard restore failed: {ex.Message}");
            return false;
        }
    }

    private static IClipboard? GetClipboard()
    {
        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop)
        {
            return null;
        }

        return desktop.MainWindow?.Clipboard
               ?? desktop.Windows.Select(window => window.Clipboard).FirstOrDefault(clipboard => clipboard is not null);
    }
}
