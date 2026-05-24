using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace TypeWhisper.Linux.Services;

/// <summary>
///     Writes ~/.config/typewhisper/api-discovery.json (XDG_CONFIG_HOME-aware)
///     so CLI clients can pick up the running app's port and bearer token
///     without configuration. The file is created when the HTTP API starts
///     and deleted when it stops.
///
///     Created with the restrictive mode applied at <c>open(2)</c> time
///     (not chmodded after content is written) so no other local user can
///     read the bearer token through a race between create and chmod. The
///     parent directory is also chmodded to 0700 so its listing doesn't
///     leak the file's existence to other local users.
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

    public static string DirectoryPath
    {
        get
        {
            var configHome = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
            if (string.IsNullOrWhiteSpace(configHome))
            {
                configHome = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    ".config"
                );
            }

            return Path.Combine(configHome, "typewhisper");
        }
    }

    public static string FilePath => Path.Combine(DirectoryPath, FileName);

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

            // Clear any stale tmp left by a crash so CreateNew below doesn't
            // collide with someone else's leftover file (potentially with
            // permissive perms inherited from a previous bad write).
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

            // Create the temp file with 0600 atomically via open(2): on
            // Linux/macOS the FileStreamOptions.UnixCreateMode is passed to
            // open()'s mode argument, so the file never exists with looser
            // perms (no chmod-after-write race). FileMode.CreateNew throws
            // if the path exists, so the stale-delete above is required.
            var options = new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                Share = FileShare.None
            };

            // UnixCreateMode is Linux/macOS-only — applying it on Windows
            // throws PNSE. Setting it conditionally keeps the same
            // create-with-restrictive-mode semantics on real deployment
            // targets without needing to suppress CA1416 broadly.
            if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
            {
                options.UnixCreateMode = FileMode0600;
            }

            using (var stream = new FileStream(tmp, options))
            using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
            {
                writer.Write(json);
            }

            // File.Move is atomic on the same Linux filesystem; overwrite the
            // existing discovery file in one syscall so a CLI client mid-read
            // never sees a half-written JSON document. The renamed inode
            // keeps the source file's 0600 perms — no re-chmod needed.
            File.Move(tmp, final, overwrite: true);
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[ApiDiscoveryFile] Write failed: {ex.Message}");
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
            // CreateDirectory honors umask, so a fresh ~/.config/typewhisper
            // ends up 0755 on default umasks. Tighten to 0700 so the bearer
            // token's filename + presence aren't observable to other local
            // users. No-op when the dir already has tighter perms.
            File.SetUnixFileMode(path, DirMode0700);
        }
        catch (Exception ex)
        {
            Trace.WriteLine(
                $"[ApiDiscoveryFile] Could not set 0700 mode on '{path}': {ex.Message}"
            );
        }
    }

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
}
