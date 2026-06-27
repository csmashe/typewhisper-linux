using System.Diagnostics;
using TypeWhisper.Core;

namespace TypeWhisper.Linux.Services;

/// <summary>
///     Copies bundled plugins from the app's install directory into
///     <c>~/.local/share/TypeWhisper/Plugins/</c>, repairing stale or partially
///     deployed bundled plugins on every startup while leaving non-bundled /
///     manually installed plugins alone.
/// </summary>
public sealed class BundledPluginDeployer
{
    // ReSharper disable once UnusedMethodReturnValue.Global -- returns the count of synced plugins for callers that want it; the current caller ignores it.
    public static int DeployIfMissing()
    {
        var source = FindBundledPluginsDir();
        if (source is null)
        {
            Trace.WriteLine(
                "[BundledPluginDeployer] No bundled Plugins/ dir next to executable — skipping."
            );
            return 0;
        }

        var destRoot = TypeWhisperEnvironment.PluginsPath;
        Directory.CreateDirectory(destRoot);

        var deployed = 0;
        foreach (var pluginDir in Directory.GetDirectories(source))
        {
            var name = Path.GetFileName(pluginDir);
            var dest = Path.Join(destRoot, name);

            try
            {
                if (NeedsRepairOrUpdate(pluginDir, dest))
                {
                    CopyDirectory(pluginDir, dest, true);
                    Trace.WriteLine(
                        $"[BundledPluginDeployer] Synced bundled plugin {name} → {dest}"
                    );
                    deployed++;
                }
                else
                {
                    Trace.WriteLine($"[BundledPluginDeployer] {name} already up to date.");
                }
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"[BundledPluginDeployer] Failed to deploy {name}: {ex.Message}");
            }
        }

        return deployed;
    }

    private static string? FindBundledPluginsDir()
    {
        var exeDir = AppContext.BaseDirectory;
        var candidate = Path.Join(exeDir, "Plugins");
        return Directory.Exists(candidate) ? candidate : null;
    }

    private static bool NeedsRepairOrUpdate(string src, string dst)
    {
        if (!Directory.Exists(dst))
        {
            return true;
        }

        foreach (var srcFile in Directory.GetFiles(src, "*", SearchOption.AllDirectories))
        {
            var relativePath = Path.GetRelativePath(src, srcFile);
            var dstFile = Path.Join(dst, relativePath);
            if (!File.Exists(dstFile))
            {
                return true;
            }

            var srcInfo = new FileInfo(srcFile);
            var dstInfo = new FileInfo(dstFile);
            if (
                srcInfo.Length != dstInfo.Length
                || srcInfo.LastWriteTimeUtc > dstInfo.LastWriteTimeUtc
            )
            {
                return true;
            }
        }

        return false;
    }

    private static void CopyDirectory(string src, string dst, bool overwrite)
    {
        Directory.CreateDirectory(dst);
        foreach (var file in Directory.GetFiles(src))
        {
            File.Copy(file, Path.Join(dst, Path.GetFileName(file)), overwrite);
        }

        foreach (var sub in Directory.GetDirectories(src))
        {
            CopyDirectory(sub, Path.Join(dst, Path.GetFileName(sub)), overwrite);
        }
    }
}