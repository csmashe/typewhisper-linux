using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.Json;

namespace TypeWhisper.Linux.Services;

/// <summary>
///     Writes ~/.config/typewhisper/api-discovery.json (XDG_CONFIG_HOME-aware)
///     so CLI clients can discover the running app's port and bearer token.
///     File is created at API start and deleted at stop. Mode 0600 is set via
///     <c>open(2)</c> (not chmod-after-write) to avoid a race exposing the token;
///     the parent directory is tightened to 0700 to hide even the file's existence.
/// </summary>
public sealed class ApiDiscoveryFile
{
    private const string FileName = "api-discovery.json";

    private const UnixFileMode FileMode0600 =
        UnixFileMode.UserRead | UnixFileMode.UserWrite;

    private const UnixFileMode DirMode0700 =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    private static readonly JsonSerializerOptions s_jsonOptions =
        new() { WriteIndented = true };

    private static string DirectoryPath
    {
        get
        {
            var configHome = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
            if (string.IsNullOrWhiteSpace(configHome))
            {
                configHome = Path.Join(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    ".config"
                );
            }

            return Path.Join(configHome, "typewhisper");
        }
    }

    private static string FilePath => Path.Join(DirectoryPath, FileName);

    // kept instance: injected as a DI/test seam by callers
    [SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "kept instance: injected as a DI/test seam")]
    // ReSharper disable once MemberCanBeMadeStatic.Global
    public void Write(int port, string token)
    {
        try
        {
            Directory.CreateDirectory(DirectoryPath);
            EnsureDirectoryMode(DirectoryPath);

            var final = FilePath;
            var tmp = final + ".tmp";
            var json = JsonSerializer.Serialize(
                new { version = 1, port, token },
                s_jsonOptions
            );

            // Clear any stale tmp from a crash; CreateNew below would otherwise
            // collide and could inherit permissive perms from the leftover.
            try
            {
                File.Delete(tmp);
            }
            catch (Exception ex)
            {
                Trace.WriteLine(
                    $"[ApiDiscoveryFile] Could not delete stale temp '{tmp}': {ex.Message}"
                );
            }

            // FileStreamOptions.UnixCreateMode is passed to open(2), so the file
            // is created 0600 atomically (no chmod-after-write race). FileMode.CreateNew
            // throws if the path exists, hence the stale-delete above.
            // UnixCreateMode is Linux/macOS-only — guard to avoid PNSE on Windows.
            var options = new FileStreamOptions
            {
                Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None,
            };

            if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
            {
                options.UnixCreateMode = FileMode0600;
            }

            using (var stream = new FileStream(tmp, options))
            using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
            {
                writer.Write(json);
            }

            // Atomic rename: a CLI client mid-read never sees a partial JSON file.
            // The renamed inode keeps its 0600 perms — no re-chmod needed.
            File.Move(tmp, final, true);
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[ApiDiscoveryFile] Write failed: {ex.Message}");
        }
    }

    // kept instance: injected as a DI/test seam by callers
    [SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "kept instance: injected as a DI/test seam")]
    // ReSharper disable once MemberCanBeMadeStatic.Global
    public void Delete()
    {
        try
        {
            File.Delete(FilePath);
        }
        catch (FileNotFoundException) { }
        catch (DirectoryNotFoundException) { }
        catch (Exception ex)
        {
            Trace.WriteLine($"[ApiDiscoveryFile] Delete failed: {ex.Message}");
        }
    }

    private static void EnsureDirectoryMode(string path)
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
        {
            return;
        }

        try
        {
            // CreateDirectory honors umask, leaving the dir 0755 by default.
            // Tighten to 0700 so the file's existence isn't visible to other users.
            File.SetUnixFileMode(path, DirMode0700);
        }
        catch (Exception ex)
        {
            Trace.WriteLine(
                $"[ApiDiscoveryFile] Could not set 0700 mode on '{path}': {ex.Message}"
            );
        }
    }
}