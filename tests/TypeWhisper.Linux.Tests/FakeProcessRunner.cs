using TypeWhisper.Linux.Services;
using TypeWhisper.PluginSDK.Processes;

namespace TypeWhisper.Linux.Tests;

/// <summary>
///     Configurable, recording <see cref="IProcessRunner" /> test double. A
///     handwritten fake is clearer than a mock framework here: tests assert on
///     <em>which</em> processes were launched and stage per-command results.
/// </summary>
internal sealed class FakeProcessRunner : IProcessRunner
{
    private readonly List<(
        Func<string, IReadOnlyList<string>, bool> Match,
        ProcessRunResult Result
        )> _overrides = [];

    public List<Invocation> Invocations { get; } = [];
    public List<SupervisorInvocation> SupervisorInvocations { get; } = [];
    public List<SessionInvocation> SessionInvocations { get; } = [];
    public List<Uri> LaunchedUris { get; } = [];

    /// <summary>Result for any call that matches no override.</summary>
    public ProcessRunResult Default { get; init; } = Success();
    public ProcessRunOutcome? SupervisorDefault { get; init; }
    // ReSharper disable once MemberCanBePrivate.Global
    // ReSharper disable once AutoPropertyCanBeMadeGetOnly.Global  init-settable like Default/SupervisorDefault; get-only would block initializer syntax.
    public DetachedLaunchOutcome DetachedDefault { get; init; } =
        new(true, null);
    public Func<
        ProcessCommand,
        ProcessSessionOptions,
        ProcessSessionStartOutcome
    >? SessionFactory { get; init; }

    /// <summary>The <c>standardInput</c> piped to the most recent invocation (e.g. a pkexec heredoc).</summary>
    // ReSharper disable once UnusedAutoPropertyAccessor.Global  recording surface for stdin assertions; kept even when a given run only checks Invocations
    public string? LastStandardInput { get; private set; }

    public Task<ProcessRunResult> RunAsync(
        string fileName,
        IReadOnlyList<string> args,
        IReadOnlyDictionary<string, string>? environment = null,
        string? standardInput = null,
        TimeSpan? timeout = null,
        bool detachAfterExit = false,
        CancellationToken ct = default
    )
    {
        Invocations.Add(new Invocation(fileName, args.ToArray(), standardInput, timeout));
        LastStandardInput = standardInput;
        foreach (var (match, result) in _overrides)
        {
            if (match(fileName, args))
            {
                return Task.FromResult(result);
            }
        }

        return Task.FromResult(Default);
    }

    public ProcessRunOutcome RunProbe(
        ProcessCommand command,
        ProcessOneShotOptions options,
        CancellationToken cancellationToken = default
    )
    {
        return RunOneShotAsync(command, options, cancellationToken)
            .GetAwaiter()
            .GetResult();
    }

    public async Task<ProcessRunOutcome> RunOneShotAsync(
        ProcessCommand command,
        ProcessOneShotOptions options,
        CancellationToken cancellationToken = default
    )
    {
        cancellationToken.ThrowIfCancellationRequested();
        SupervisorInvocations.Add(new SupervisorInvocation(command, options));
        if (SupervisorDefault is not null)
        {
            return SupervisorDefault;
        }

        var legacy = await RunAsync(
            command.FileName,
            command.Arguments,
            command.Environment,
            (options.StandardInput as Utf8ProcessInput)?.Value,
            options.Timeout,
            options.PostExitPipePolicy == ProcessPostExitPipePolicy.AbandonAfterGrace,
            cancellationToken
        );
        var status = legacy.Started
            ? legacy.TimedOut
                ? ProcessRunStatus.TimedOut
                : ProcessRunStatus.Exited
            : ProcessRunStatus.StartFailed;
        return new ProcessRunOutcome(
            status,
            status == ProcessRunStatus.Exited ? legacy.ExitCode : null,
            options.StandardOutput == ProcessCaptureMode.Discard
                ? []
                : System.Text.Encoding.UTF8.GetBytes(legacy.StandardOutput),
            options.StandardError == ProcessCaptureMode.Discard
                ? []
                : System.Text.Encoding.UTF8.GetBytes(legacy.StandardError),
            ProcessOutputStatus.Complete,
            status == ProcessRunStatus.StartFailed ? legacy.StandardError : null
        );
    }

    public ProcessSessionStartOutcome StartSession(
        ProcessCommand command,
        ProcessSessionOptions options
    )
    {
        SessionInvocations.Add(new SessionInvocation(command, options));
        return SessionFactory?.Invoke(command, options)
               ?? throw new NotSupportedException(
                   "This fake has no session configured."
               );
    }

    public DetachedLaunchOutcome LaunchDetached(ProcessCommand command)
    {
        return DetachedDefault;
    }

    public DetachedLaunchOutcome LaunchUri(Uri uri)
    {
        LaunchedUris.Add(uri);
        return DetachedDefault;
    }

    /// <summary>Make calls matching <paramref name="match" /> exit non-zero.</summary>
    public void FailWhen(Func<string, IReadOnlyList<string>, bool> match, string stderr = "")
    {
        _overrides.Add(
            (
                match,
                new ProcessRunResult(
                    true,
                    false,
                    1,
                    string.Empty,
                    stderr
                )
            )
        );
    }

    /// <summary>
    ///     Make calls matching <paramref name="match" /> return the given non-zero
    ///     exit code (e.g. 126/127 for a dismissed pkexec auth prompt). The result
    ///     is flagged started-and-not-timed-out so only the exit code drives the
    ///     caller's branch.
    /// </summary>
    public void SetExitCode(Func<string, IReadOnlyList<string>, bool> match, int exitCode)
    {
        _overrides.Add(
            (
                match,
                new ProcessRunResult(
                    true,
                    false,
                    exitCode,
                    string.Empty,
                    string.Empty
                )
            )
        );
    }

    /// <summary>Make calls matching <paramref name="match" /> succeed with the given stdout.</summary>
    public void RespondWith(Func<string, IReadOnlyList<string>, bool> match, string stdout)
    {
        _overrides.Add(
            (
                match,
                new ProcessRunResult(
                    true,
                    false,
                    0,
                    stdout,
                    string.Empty
                )
            )
        );
    }

    private static ProcessRunResult Success(string stdout = "")
    {
        return new ProcessRunResult(
            true,
            false,
            0,
            stdout,
            string.Empty
        );
    }

    /// <summary>Models a process that could not be launched at all (e.g. binary missing).</summary>
    public static ProcessRunResult NotStarted()
    {
        return new ProcessRunResult(
            false,
            false,
            -1,
            string.Empty,
            "fake: process not started"
        );
    }

    public sealed record Invocation(
        string FileName,
        IReadOnlyList<string> Args,
        // ReSharper disable once NotAccessedPositionalProperty.Global  part of the recorded invocation shape; available for stdin assertions
        string? StandardInput = null,
        TimeSpan? Timeout = null
    );

    public sealed record SupervisorInvocation(
        ProcessCommand Command,
        ProcessOneShotOptions Options
    );

    public sealed record SessionInvocation(
        ProcessCommand Command,
        ProcessSessionOptions Options
    );
}
