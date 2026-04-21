using System.Diagnostics;
using System.IO;
using TypeWhisper.Core;

namespace TypeWhisper.Linux.Services;

/// <summary>
/// First-run onboarding: copies bundled plugins from the app's install
/// directory (e.g. next to the executable under <c>Plugins/</c>) into
/// <c>~/.local/share/TypeWhisper/Plugins/</c>. Skips any plugin folder
/// that already exists at the destination, so manual installs are
/// preserved and upgrades don't clobber user customization.
///
/// The Windows shell uses a more elaborate PluginRegistryService that
/// talks to a remote marketplace; for Linux v1 we ship a curated bundle
/// (SherpaOnnx, WhisperCpp, FileMemory) alongside the app and let the
/// user install more manually until a proper registry client exists.
/// </summary>
public sealed class BundledPluginDeployer
{
    public int DeployIfMissing()
    {
        var source = FindBundledPluginsDir();
        if (source is null)
        {
            Trace.WriteLine("[BundledPluginDeployer] No bundled Plugins/ dir next to executable — skipping.");
            return 0;
        }

        var destRoot = TypeWhisperEnvironment.PluginsPath;
        Directory.CreateDirectory(destRoot);

        var deployed = 0;
        foreach (var pluginDir in Directory.GetDirectories(source))
        {
            var name = Path.GetFileName(pluginDir);
            var dest = Path.Combine(destRoot, name);
            if (Directory.Exists(dest))
            {
                Trace.WriteLine($"[BundledPluginDeployer] {name} already installed — skipping.");
                continue;
            }

            try
            {
                CopyDirectory(pluginDir, dest);
                Trace.WriteLine($"[BundledPluginDeployer] Deployed {name} → {dest}");
                deployed++;
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
        var candidate = Path.Combine(exeDir, "Plugins");
        return Directory.Exists(candidate) ? candidate : null;
    }

    private static void CopyDirectory(string src, string dst)
    {
        Directory.CreateDirectory(dst);
        foreach (var file in Directory.GetFiles(src))
            File.Copy(file, Path.Combine(dst, Path.GetFileName(file)), overwrite: false);
        foreach (var sub in Directory.GetDirectories(src))
            CopyDirectory(sub, Path.Combine(dst, Path.GetFileName(sub)));
    }
}
