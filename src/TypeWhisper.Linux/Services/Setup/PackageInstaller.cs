using System.Diagnostics.CodeAnalysis;
using TypeWhisper.Linux.Services.Hotkey.DeSetup;
using TypeWhisper.Linux.Services.Localization;

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
///     Detects the host's package manager and installs packages via
///     <c>pkexec</c>. Prefers the manager named by <c>/etc/os-release</c>,
///     falls back to probing PATH. When no manager or pkexec is found, callers
///     fall back to showing the copyable <see cref="BuildSudoCommand" /> output.
/// </summary>
public sealed class PackageInstaller
{
    // Ordered by how we'd prefer to resolve ties when probing PATH.
    private static readonly PackageManager[] s_known =
    [
        new("dnf", "dnf", ["install", "-y"]), new("apt", "apt-get", ["install", "-y"]),
        new("pacman", "pacman", ["-S", "--noconfirm"]),
        new("zypper", "zypper", ["--non-interactive", "install"]),
    ];

    private readonly IProcessRunner _runner;

    public PackageInstaller(IProcessRunner runner)
    {
        _runner = runner;
    }

    /// <summary>
    ///     Resolve the package manager for this host, or null if none of the
    ///     managers we know how to drive are present.
    /// </summary>
    private static PackageManager? Detect()
    {
        var hinted = ReadOsReleaseManagerHints()
            .Select(id => s_known.FirstOrDefault(m => m.Id == id))
            .FirstOrDefault(match => match is not null && DesktopDetector.BinaryExists(match.Binary));
        // os-release didn't point at an installed manager — probe PATH.
        return hinted ?? s_known.FirstOrDefault(m => DesktopDetector.BinaryExists(m.Binary));
    }

    /// <summary>
    ///     The copyable, terminal-ready command for installing
    ///     <paramref name="packages" /> — shown when one-click install isn't
    ///     possible. Falls back to a generic placeholder when no manager is
    ///     detected so the user still sees what they need to install.
    /// </summary>
    // kept instance: invoked on the injected _installer service by callers
    [SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "kept instance: injected as a DI/test seam")]
    // ReSharper disable once MemberCanBeMadeStatic.Global
    public string BuildSudoCommand(IReadOnlyList<string> packages)
    {
        return BuildSudoCommand(packages, Detect());
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
        var pm = Detect();
        var fallback = BuildSudoCommand(packages, pm);
        if (pm is null)
        {
            return new SetupActionOutcome(
                false,
                Loc.Instance["Setup.NoPackageManager"],
                Loc.Instance.GetString("Setup.InstallItManually", fallback)
            );
        }

        if (!DesktopDetector.BinaryExists("pkexec"))
        {
            return new SetupActionOutcome(
                false,
                Loc.Instance["Setup.PkexecUnavailable"],
                Loc.Instance.GetString("Setup.RunInTerminalInstead", fallback)
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
                Loc.Instance.GetString("Setup.InstalledPackages", string.Join(", ", packages))
            );
        }

        // 126 = authorization denied/dismissed; 127 = program not runnable.
        if (result.ExitCode is 126 or 127)
        {
            return new SetupActionOutcome(
                false,
                Loc.Instance["Setup.AdminAuthCancelled"],
                Loc.Instance.GetString("Setup.YouCanAlsoRun", fallback)
            );
        }

        var detail = string.IsNullOrWhiteSpace(result.StandardError)
            ? Loc.Instance.GetString("Setup.RunInTerminalInstead", fallback)
            : result.StandardError.Trim();
        return new SetupActionOutcome(
            false,
            Loc.Instance.GetString("Setup.InstallFailed", result.ExitCode),
            detail
        );
    }

    private static string BuildSudoCommand(IReadOnlyList<string> packages, PackageManager? pm)
    {
        var joined = string.Join(' ', packages);
        return pm is null
            ? $"sudo <your package manager> install {joined}"
            : $"sudo {pm.Binary} {string.Join(' ', pm.InstallArgs)} {joined}";
    }

    /// <summary>Yields package-manager ids from /etc/os-release: ID first, then ID_LIKE tokens.</summary>
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
                tokens.Insert(0, Unquote(line[3..]));
            }
            else if (line.StartsWith("ID_LIKE=", StringComparison.Ordinal))
            {
                tokens.AddRange(
                    Unquote(line[8..])
                        .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                );
            }
        }

        foreach (var manager in tokens.Select(MapDistroToManager).OfType<string>())
        {
            yield return manager;
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
            _ => null,
        };
    }

    private static string Unquote(string raw)
    {
        var s = raw.Trim();
        if (s.Length >= 2 && (s[0] == '"' || s[0] == '\'') && s[^1] == s[0])
        {
            s = s[1..^1];
        }

        return s;
    }
}