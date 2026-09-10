using System.Diagnostics;
using System.Text;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Processes;

namespace TypeWhisper.Plugin.AuthenticatedCli;

internal sealed record CliProcessRequest(
    string ExecutablePath,
    IReadOnlyList<string> Arguments,
    string StandardInput,
    string WorkingDirectory,
    IReadOnlyList<string> ProviderEnvironmentVariables,
    TimeSpan Timeout,
    int MaximumStandardOutputBytes,
    int MaximumStandardErrorBytes,
    IReadOnlyDictionary<string, string>? EnvironmentOverrides = null
);

internal sealed record CliProcessResult(
    int ExitCode,
    string StandardOutput,
    string StandardError,
    TimeSpan Elapsed,
    int StandardOutputBytes,
    int StandardErrorBytes
);

internal interface ICliProcessRunner
{
    Task<CliProcessResult> RunAsync(CliProcessRequest request, CancellationToken cancellationToken);
}

/// <summary>
///     Runs a provider CLI through the host's per-plugin process supervisor with an environment
///     built from an allow-list, so nothing but the variables the CLI needs to find its own login
///     reaches the child.
/// </summary>
internal sealed class CliProcessRunner(IPluginProcessSupervisor supervisor) : ICliProcessRunner
{
    private static readonly Encoding s_strictUtf8 = new UTF8Encoding(false, true);

    // Forced by the runner, so an override that targets one is a programming error, not a setting.
    private static readonly HashSet<string> s_protectedEnvironmentVariables = new(
        ["PATH", "TMPDIR"],
        StringComparer.Ordinal
    );

    private static readonly string[] s_commonEnvironmentVariables =
    [
        "HOME",
        "XDG_CONFIG_HOME",
        "XDG_DATA_HOME",
        "XDG_CACHE_HOME",
        "XDG_STATE_HOME",
        "LANG",
        "LC_ALL",
        "HTTP_PROXY",
        "HTTPS_PROXY",
        "http_proxy",
        "https_proxy",
        "ALL_PROXY",
        "NO_PROXY",
        "no_proxy",
        "SSL_CERT_FILE",
        "SSL_CERT_DIR",
    ];

    // Auth stores the user points elsewhere. Accepted only when they name a real local directory.
    private static readonly string[] s_authenticationStoreVariables =
    [
        "CODEX_HOME",
        "CLAUDE_CONFIG_DIR",
        "XDG_DATA_HOME",
    ];

    public async Task<CliProcessResult> RunAsync(
        CliProcessRequest request,
        CancellationToken cancellationToken
    )
    {
        var stopwatch = Stopwatch.StartNew();
        var outcome = await supervisor.RunOneShotAsync(
            CreateProcessCommand(request),
            new ProcessOneShotOptions(
                Timeout: request.Timeout,
                StandardInput: new Utf8ProcessInput(request.StandardInput),
                StandardOutput: ProcessCaptureMode.Binary,
                StandardError: ProcessCaptureMode.Binary,
                MaximumStandardOutputBytes: request.MaximumStandardOutputBytes,
                MaximumStandardErrorBytes: request.MaximumStandardErrorBytes,
                // Every request and probe goes through here, so the allow-list below is the
                // child's whole environment rather than an addition to the host's.
                ClearInheritedEnvironment: true
            ),
            cancellationToken
        ).ConfigureAwait(false);
        stopwatch.Stop();

        switch (outcome.Status)
        {
            case ProcessRunStatus.TimedOut:
                throw new PluginRequestException(
                    "The provider CLI timed out.",
                    PluginRequestFailureKind.Timeout,
                    isTransient: false
                );
            case ProcessRunStatus.StartFailed:
                // The launcher's own diagnostic is usually the only clue (a missing interpreter
                // for a #! script, for instance), so it travels with the failure.
                throw new PluginRequestException(
                    string.IsNullOrWhiteSpace(outcome.StartError)
                        ? "The provider CLI could not be started."
                        : $"The provider CLI could not be started: {outcome.StartError.Trim()}",
                    PluginRequestFailureKind.Configuration,
                    isTransient: false
                );
            case ProcessRunStatus.OutputLimitExceeded:
                throw new PluginRequestException(
                    "The provider CLI produced too much output.",
                    PluginRequestFailureKind.Unknown,
                    isTransient: false
                );
            case ProcessRunStatus.Exited:
            default:
                // A clean exit falls through to decoding below, where a non-zero exit code is
                // classified from the CLI's own output.
                break;
        }

        try
        {
            return new CliProcessResult(
                outcome.ExitCode ?? -1,
                s_strictUtf8.GetString(outcome.StandardOutput),
                s_strictUtf8.GetString(outcome.StandardError),
                stopwatch.Elapsed,
                outcome.StandardOutput.Length,
                outcome.StandardError.Length
            );
        }
        catch (DecoderFallbackException ex)
        {
            throw new PluginRequestException(
                "The provider CLI returned output that was not valid UTF-8.",
                PluginRequestFailureKind.Unknown,
                isTransient: false,
                innerException: ex
            );
        }
    }

    internal static ProcessCommand CreateProcessCommand(CliProcessRequest request)
    {
        var executablePath = Path.GetFullPath(request.ExecutablePath);
        var workingDirectory = Path.GetFullPath(request.WorkingDirectory);
        var environment = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var name in s_commonEnvironmentVariables
                     .Concat(request.ProviderEnvironmentVariables)
                     .Distinct(StringComparer.Ordinal))
        {
            var value = Environment.GetEnvironmentVariable(name);
            if (string.IsNullOrEmpty(value))
            {
                continue;
            }

            if (s_authenticationStoreVariables.Contains(name, StringComparer.Ordinal)
                && !CliPathSafety.IsSafeLocalDirectory(value))
            {
                continue;
            }

            environment[name] = value;
        }

        var executableDirectory = Path.GetDirectoryName(executablePath)!;
        environment["PATH"] = string.Join(
            ':',
            executableDirectory,
            "/usr/local/bin",
            "/usr/bin",
            "/bin"
        );
        environment["TMPDIR"] = workingDirectory;
        environment["NO_COLOR"] = "1";
        environment["CI"] = "1";
        environment["TERM"] = "dumb";
        if (request.ProviderEnvironmentVariables.Contains("CLAUDE_CONFIG_DIR", StringComparer.Ordinal))
        {
            environment["CLAUDE_CODE_SKIP_PROMPT_HISTORY"] = "1";
        }

        // ReSharper disable once InvertIf
        // Inverting would have to duplicate the ProcessCommand construction below.
        if (request.EnvironmentOverrides is not null)
        {
            foreach (var (name, value) in request.EnvironmentOverrides)
            {
                ValidateEnvironmentOverride(name, value);
                environment[name] = value;
            }
        }

        return new ProcessCommand(
            executablePath,
            request.Arguments,
            environment,
            workingDirectory
        );
    }

    private static void ValidateEnvironmentOverride(string name, string value)
    {
        if (string.IsNullOrWhiteSpace(name)
            || name.Contains('=')
            || name.Contains('\0')
            || name.Any(character => !(char.IsAsciiLetterOrDigit(character) || character == '_'))
            || !(char.IsAsciiLetter(name[0]) || name[0] == '_'))
        {
            throw new InvalidOperationException(
                "The provider CLI environment override name is invalid."
            );
        }

        if (s_protectedEnvironmentVariables.Contains(name))
        {
            throw new InvalidOperationException(
                "The provider CLI environment override targets a protected variable."
            );
        }

        if (value.Contains('\0'))
        {
            throw new InvalidOperationException(
                "The provider CLI environment override value is invalid."
            );
        }
    }
}
