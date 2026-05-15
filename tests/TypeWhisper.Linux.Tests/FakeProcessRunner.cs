using TypeWhisper.Linux.Services;

namespace TypeWhisper.Linux.Tests;

/// <summary>
/// Configurable, recording <see cref="IProcessRunner"/> test double. A
/// hand-written fake is clearer than a mock framework here: tests assert on
/// <em>which</em> processes were launched and stage per-command results.
/// </summary>
internal sealed class FakeProcessRunner : IProcessRunner
{
    public sealed record Invocation(string FileName, IReadOnlyList<string> Args);

    public List<Invocation> Invocations { get; } = [];

    private readonly List<(Func<string, IReadOnlyList<string>, bool> Match, ProcessRunResult Result)> _overrides = [];

    /// <summary>Result for any call that matches no override.</summary>
    public ProcessRunResult Default { get; set; } = Success();

    /// <summary>Make calls matching <paramref name="match"/> exit non-zero.</summary>
    public void FailWhen(Func<string, IReadOnlyList<string>, bool> match, string stderr = "")
        => _overrides.Add((match, new ProcessRunResult(
            Started: true, TimedOut: false, ExitCode: 1,
            StandardOutput: string.Empty, StandardError: stderr)));

    /// <summary>Make calls matching <paramref name="match"/> succeed with the given stdout.</summary>
    public void RespondWith(Func<string, IReadOnlyList<string>, bool> match, string stdout)
        => _overrides.Add((match, new ProcessRunResult(
            Started: true, TimedOut: false, ExitCode: 0,
            StandardOutput: stdout, StandardError: string.Empty)));

    public Task<ProcessRunResult> RunAsync(
        string fileName,
        IReadOnlyList<string> args,
        IReadOnlyDictionary<string, string>? environment = null,
        string? standardInput = null,
        TimeSpan? timeout = null,
        CancellationToken ct = default)
    {
        Invocations.Add(new Invocation(fileName, args.ToArray()));
        foreach (var (match, result) in _overrides)
        {
            if (match(fileName, args))
                return Task.FromResult(result);
        }
        return Task.FromResult(Default);
    }

    public static ProcessRunResult Success(string stdout = "") =>
        new(Started: true, TimedOut: false, ExitCode: 0, StandardOutput: stdout, StandardError: string.Empty);

    /// <summary>Models a process that could not be launched at all (e.g. binary missing).</summary>
    public static ProcessRunResult NotStarted() =>
        new(Started: false, TimedOut: false, ExitCode: -1,
            StandardOutput: string.Empty, StandardError: "fake: process not started");
}
