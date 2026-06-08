using TypeWhisper.Linux.Services.Hotkey.DeSetup;

namespace TypeWhisper.Linux.Services.Setup;

/// <summary>
///     A detected system package manager and how to drive a non-interactive
///     install through it.
/// </summary>
/// <param name="Id">Stable token ("dnf", "apt", "pacman", "zypper").</param>
/// <param name="Binary">The manager executable to invoke.</param>
/// <param name="InstallArgs">
///     Argument prefix for a non-interactive install — the package names are
///     appended after these.
/// </param>
public sealed record PackageManager(string Id, string Binary, IReadOnlyList<string> InstallArgs);

/// <summary>
///     Detects the host's package manager and installs packages on the user's
///     behalf via <c>pkexec</c> (the single graphical admin-consent prompt).
///     Detection prefers the manager named by <c>/etc/os-release</c> but falls
///     back to whichever known manager binary is on PATH, so an exotic
///     <c>ID_LIKE</c> still resolves. When no manager can be found — or
///     <c>pkexec</c> is missing — callers fall back to showing the copyable
///     command from <see cref="BuildSudoCommand" />.
///
///     This is the cross-distro half of the "one-click install" path: every
///     setup task that needs a package routes through here rather than
///     hard-coding a distro, keeping the tasks themselves machine-agnostic.
/// </summary>
public sealed class PackageInstaller
{
    // Ordered by how we'd prefer to resolve ties when probing PATH.
    private static readonly PackageManager[] Known =
    {
        new("dnf", "dnf", new[] { "install", "-y" }),
        new("apt", "apt-get", new[] { "install", "-y" }),
        new("pacman", "pacman", new[] { "-S", "--noconfirm" }),
        new("zypper", "zypper", new[] { "--non-interactive", "install" })
    };

    private readonly IProcessRunner _runner;

    public PackageInstaller(IProcessRunner runner)
    {
        _runner = runner;
    }

    /// <summary>
    ///     Resolve the package manager for this host, or null if none of the
    ///     managers we know how to drive are present.
    /// </summary>
    public PackageManager? Detect()
    {
        foreach (var id in ReadOsReleaseManagerHints())
        {
            var match = Known.FirstOrDefault(m => m.Id == id);
            if (match is not null && DesktopDetector.BinaryExists(match.Binary))
            {
                return match;
            }
        }

        // os-release didn't point at an installed manager — probe PATH.
        return Known.FirstOrDefault(m => DesktopDetector.BinaryExists(m.Binary));
    }

    /// <summary>
    ///     The copyable, terminal-ready command for installing
    ///     <paramref name="packages" /> — shown when one-click install isn't
    ///     possible. Falls back to a generic placeholder when no manager is
    ///     detected so the user still sees what they need to install.
    /// </summary>
    public string BuildSudoCommand(IReadOnlyList<string> packages)
    {
        var joined = string.Join(' ', packages);
        var pm = Detect();
        if (pm is null)
        {
            return $"sudo <your package manager> install {joined}";
        }

        return $"sudo {pm.Binary} {string.Join(' ', pm.InstallArgs)} {joined}";
    }

    /// <summary>
    ///     Install <paramref name="packages" /> via <c>pkexec</c>. Returns a
    ///     human-readable outcome; on failure the message points the user at
    ///     the copyable command. Never throws for the expected failure paths
    ///     (no manager, no pkexec, auth dismissed, non-zero exit).
    /// </summary>
    public async Task<SetupActionOutcome> InstallAsync(
        IReadOnlyList<string> packages,
        CancellationToken ct
    )
    {
        var fallback = BuildSudoCommand(packages);
        var pm = Detect();
        if (pm is null)
        {
            return new SetupActionOutcome(
                false,
                "Could not detect your package manager.",
                $"Install it manually: {fallback}"
            );
        }

        if (!DesktopDetector.BinaryExists("pkexec"))
        {
            return new SetupActionOutcome(
                false,
                "pkexec is not available to request admin rights.",
                $"Run this in a terminal instead: {fallback}"
            );
        }

        var args = new List<string> { pm.Binary };
        args.AddRange(pm.InstallArgs);
        args.AddRange(packages);

        var result = await _runner
            .RunAsync("pkexec", args, timeout: TimeSpan.FromMinutes(5), ct: ct)
            .ConfigureAwait(false);

        if (result.Succeeded)
        {
            return new SetupActionOutcome(
                true,
                $"Installed {string.Join(", ", packages)}."
            );
        }

        // pkexec uses 126 for "authorization could not be obtained" and 127
        // when the requested program can't be run — both usually mean the
        // user dismissed the prompt or it timed out.
        if (result.ExitCode is 126 or 127)
        {
            return new SetupActionOutcome(
                false,
                "Admin authorization was cancelled or denied.",
                $"You can also run: {fallback}"
            );
        }

        var detail = string.IsNullOrWhiteSpace(result.StandardError)
            ? $"Run this in a terminal instead: {fallback}"
            : result.StandardError.Trim();
        return new SetupActionOutcome(
            false,
            $"Install failed (exit {result.ExitCode}).",
            detail
        );
    }

    /// <summary>
    ///     Yield package-manager ids hinted by <c>/etc/os-release</c>, in
    ///     priority order: the distro's own <c>ID</c> first, then each
    ///     <c>ID_LIKE</c> token. Maps distro ids onto our manager ids.
    /// </summary>
    private static IEnumerable<string> ReadOsReleaseManagerHints()
    {
        string[] lines;
        try
        {
            if (!File.Exists("/etc/os-release"))
            {
                yield break;
            }

            lines = File.ReadAllLines("/etc/os-release");
        }
        catch
        {
            yield break;
        }

        var tokens = new List<string>();
        foreach (var line in lines)
        {
            if (line.StartsWith("ID=", StringComparison.Ordinal))
            {
                tokens.Insert(0, Unquote(line.Substring(3)));
            }
            else if (line.StartsWith("ID_LIKE=", StringComparison.Ordinal))
            {
                tokens.AddRange(
                    Unquote(line.Substring(8))
                        .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                );
            }
        }

        foreach (var token in tokens)
        {
            var manager = MapDistroToManager(token);
            if (manager is not null)
            {
                yield return manager;
            }
        }
    }

    private static string? MapDistroToManager(string distroId)
    {
        return distroId.ToLowerInvariant() switch
        {
            "fedora" or "rhel" or "centos" or "rocky" or "almalinux" or "nobara" => "dnf",
            "debian" or "ubuntu" or "linuxmint" or "pop" or "raspbian" => "apt",
            "arch" or "manjaro" or "endeavouros" or "garuda" or "cachyos" => "pacman",
            "opensuse" or "opensuse-leap" or "opensuse-tumbleweed" or "sles" or "suse" => "zypper",
            _ => null
        };
    }

    private static string Unquote(string raw)
    {
        var s = raw.Trim();
        if (s.Length >= 2 && (s[0] == '"' || s[0] == '\'') && s[^1] == s[0])
        {
            s = s.Substring(1, s.Length - 2);
        }

        return s;
    }
}
