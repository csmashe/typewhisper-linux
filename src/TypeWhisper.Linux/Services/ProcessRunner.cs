using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using TypeWhisper.PluginSDK.Processes;

namespace TypeWhisper.Linux.Services;

/// <summary>
///     Compatibility outcome for callers using the original process-runner API.
/// </summary>
public sealed record ProcessRunResult(
    bool Started,
    bool TimedOut,
    int ExitCode,
    string StandardOutput,
    string StandardError
)
{
    public bool Succeeded => Started && !TimedOut && ExitCode == 0;

    internal static ProcessRunResult NotStarted(string error)
    {
        return new ProcessRunResult(false, false, -1, string.Empty, error);
    }
}

/// <summary>
///     Linux compatibility interface. New code should use the supervisor members inherited
///     from <see cref="IPluginProcessSupervisor"/>; <see cref="RunAsync"/> remains while
///     established callers migrate.
/// </summary>
public interface IProcessRunner : IPluginProcessSupervisor
{
    Task<ProcessRunResult> RunAsync(
        string fileName,
        IReadOnlyList<string> args,
        IReadOnlyDictionary<string, string>? environment = null,
        string? standardInput = null,
        TimeSpan? timeout = null,
        bool detachAfterExit = false,
        CancellationToken ct = default
    );

    ProcessRunOutcome IPluginProcessSupervisor.RunProbe(
        ProcessCommand command,
        ProcessOneShotOptions options,
        CancellationToken cancellationToken
    )
    {
        return RunOneShotAsync(command, options, cancellationToken)
            .GetAwaiter()
            .GetResult();
    }

    async Task<ProcessRunOutcome> IPluginProcessSupervisor.RunOneShotAsync(
        ProcessCommand command,
        ProcessOneShotOptions options,
        CancellationToken cancellationToken
    )
    {
        if (options.StandardInput is BinaryProcessInput)
        {
            throw new NotSupportedException(
                "This legacy process runner does not support binary standard input."
            );
        }

        if (
            options.StandardOutput == ProcessCaptureMode.Binary
            || options.StandardError == ProcessCaptureMode.Binary
        )
        {
            throw new NotSupportedException(
                "This legacy process runner does not support binary process capture."
            );
        }

        var legacy = await RunAsync(
                command.FileName,
                command.Arguments,
                command.Environment,
                (options.StandardInput as Utf8ProcessInput)?.Value,
                options.Timeout,
                options.PostExitPipePolicy == ProcessPostExitPipePolicy.AbandonAfterGrace,
                cancellationToken
            )
            .ConfigureAwait(false);

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

    ProcessSessionStartOutcome IPluginProcessSupervisor.StartSession(
        ProcessCommand command,
        ProcessSessionOptions options
    )
    {
        throw new NotSupportedException(
            "This legacy process runner does not support long-lived sessions."
        );
    }

    DetachedLaunchOutcome IPluginProcessSupervisor.LaunchDetached(ProcessCommand command)
    {
        throw new NotSupportedException(
            "This legacy process runner does not support detached launches."
        );
    }

    DetachedLaunchOutcome IPluginProcessSupervisor.LaunchUri(Uri uri)
    {
        throw new NotSupportedException(
            "This legacy process runner does not support URI launches."
        );
    }
}

/// <summary>
///     The sole production owner of launched child processes.
/// </summary>
public sealed class ProcessRunner : IProcessRunner
{
    private static readonly TimeSpan s_cleanupGrace = TimeSpan.FromMilliseconds(250);

    public ProcessRunOutcome RunProbe(
        ProcessCommand command,
        ProcessOneShotOptions options,
        CancellationToken cancellationToken = default
    )
    {
        return RunOneShotAsync(command, options, cancellationToken).GetAwaiter().GetResult();
    }

    public async Task<ProcessRunOutcome> RunOneShotAsync(
        ProcessCommand command,
        ProcessOneShotOptions options,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(options);
        // Validated before the child exists: an unusable delay must not surface only after a
        // side-effecting command has already run, with its pumps left waiting.
        ValidateDelay(options.Timeout, nameof(options.Timeout));
        ValidateDelay(options.PostExitDrainGrace, nameof(options.PostExitDrainGrace));
        cancellationToken.ThrowIfCancellationRequested();

        Process? process;
        try
        {
            process = StartContainedProcess(CreateOneShotStartInfo(command, options));
        }
        catch (Exception ex)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return StartFailed(ex.Message);
        }

        if (process is null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return StartFailed($"Could not start {command.FileName}");
        }

        using (process)
        using (var timeoutCts = options.Timeout is not null
                   ? new CancellationTokenSource(options.Timeout.Value)
                   : null)
        using (var lifecycleCts = timeoutCts is not null
                   ? CancellationTokenSource.CreateLinkedTokenSource(
                       cancellationToken,
                       timeoutCts.Token
                   )
                   : null)
        {
            // The private deadline is armed above before pumps, input, or exit waiting begin.
            var lifecycleToken = lifecycleCts?.Token ?? cancellationToken;
            var stdout = new CapturedPipe(options.StandardOutput);
            var stderr = new CapturedPipe(options.StandardError);
            var stdoutTask = PumpAsync(process.StandardOutput.BaseStream, stdout);
            var stderrTask = PumpAsync(process.StandardError.BaseStream, stderr);
            var inputTask = WriteInputAsync(process, options.StandardInput, lifecycleToken);
            var exitTask = process.WaitForExitAsync(lifecycleToken);

            try
            {
                await Task.WhenAll(exitTask, inputTask).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // WhenAll reports a fault ahead of a sibling's cancellation, so an unexpected
                // stdin failure would otherwise escape with the deadline unenforced and the
                // child still running: every post-start exit reaps before leaving.
                await TerminateAndReapAsync(process).ConfigureAwait(false);
                await AbandonPumpsAsync(process, stdoutTask, stderrTask).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                if (ex is not OperationCanceledException)
                {
                    throw;
                }

                return TimedOut(stdout, stderr);
            }

            var exitCode = process.ExitCode;
            if (options.PostExitPipePolicy == ProcessPostExitPipePolicy.AbandonAfterGrace)
            {
                var drainGrace = options.PostExitDrainGrace ?? s_cleanupGrace;
                var allPumps = Task.WhenAll(stdoutTask, stderrTask);
                try
                {
                    await allPumps.WaitAsync(drainGrace, cancellationToken).ConfigureAwait(false);
                }
                catch (TimeoutException)
                {
                    await AbandonPumpsAsync(process, stdoutTask, stderrTask).ConfigureAwait(false);
                    // A stubborn inherited pipe keeps the cleanup above running for its whole
                    // grace; a cancel landing in that window must not be reported as success.
                    cancellationToken.ThrowIfCancellationRequested();
                    return Exited(
                        exitCode,
                        stdout,
                        stderr,
                        ProcessOutputStatus.AbandonedAfterExit
                    );
                }
                catch (OperationCanceledException)
                {
                    await TerminateAndReapAsync(process).ConfigureAwait(false);
                    await AbandonPumpsAsync(process, stdoutTask, stderrTask).ConfigureAwait(false);
                    throw;
                }
            }
            else
            {
                try
                {
                    await Task.WhenAll(stdoutTask, stderrTask)
                        .WaitAsync(lifecycleToken)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    await TerminateAndReapAsync(process).ConfigureAwait(false);
                    await AbandonPumpsAsync(process, stdoutTask, stderrTask).ConfigureAwait(false);
                    cancellationToken.ThrowIfCancellationRequested();
                    return TimedOut(stdout, stderr);
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            return Exited(exitCode, stdout, stderr, ProcessOutputStatus.Complete);
        }
    }

    public ProcessSessionStartOutcome StartSession(
        ProcessCommand command,
        ProcessSessionOptions options
    )
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(options);
        try
        {
            var process = StartContainedProcess(CreateSessionStartInfo(command, options));
            return process is null
                ? new ProcessSessionStartOutcome(
                    null,
                    $"Could not start {command.FileName}"
                )
                : new ProcessSessionStartOutcome(
                    new ProcessSession(process, options),
                    null
                );
        }
        catch (Exception ex)
        {
            return new ProcessSessionStartOutcome(null, ex.Message);
        }
    }

    public DetachedLaunchOutcome LaunchDetached(ProcessCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        try
        {
            var process = StartDetachedProcess(CreateDetachedStartInfo(command));
            if (process is null)
            {
                return new DetachedLaunchOutcome(
                    false,
                    $"Could not start {command.FileName}"
                );
            }

            ObserveDetachedLauncher(process);
            return new DetachedLaunchOutcome(true, null);
        }
        catch (Exception ex)
        {
            return new DetachedLaunchOutcome(false, ex.Message);
        }
    }

    public DetachedLaunchOutcome LaunchUri(Uri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);
        try
        {
            var process = StartDetachedProcess(
                new ProcessStartInfo(uri.AbsoluteUri)
                {
                    UseShellExecute = true,
                }
            );
            if (process is null)
            {
                return new DetachedLaunchOutcome(
                    false,
                    $"Could not launch {uri.AbsoluteUri}"
                );
            }

            ObserveDetachedLauncher(process);
            return new DetachedLaunchOutcome(true, null);
        }
        catch (Exception ex)
        {
            return new DetachedLaunchOutcome(false, ex.Message);
        }
    }

    public async Task<ProcessRunResult> RunAsync(
        string fileName,
        IReadOnlyList<string> args,
        IReadOnlyDictionary<string, string>? environment = null,
        string? standardInput = null,
        TimeSpan? timeout = null,
        bool detachAfterExit = false,
        CancellationToken ct = default
    )
    {
        var outcome = await RunOneShotAsync(
                new ProcessCommand(fileName, args, environment),
                new ProcessOneShotOptions(
                    timeout,
                    standardInput is null ? null : new Utf8ProcessInput(standardInput),
                    ProcessCaptureMode.Utf8Text,
                    ProcessCaptureMode.Utf8Text,
                    detachAfterExit
                        ? ProcessPostExitPipePolicy.AbandonAfterGrace
                        : ProcessPostExitPipePolicy.RequireEof
                ),
                ct
            )
            .ConfigureAwait(false);

        return outcome.Status switch
        {
            ProcessRunStatus.StartFailed => ProcessRunResult.NotStarted(
                outcome.StartError ?? $"Could not start {fileName}"
            ),
            ProcessRunStatus.TimedOut => new ProcessRunResult(
                true,
                true,
                -1,
                string.Empty,
                string.Empty
            ),
            _ => new ProcessRunResult(
                true,
                false,
                outcome.ExitCode ?? -1,
                outcome.StandardOutputText,
                outcome.StandardErrorText
            ),
        };
    }

    private static void ValidateDelay(TimeSpan? delay, string name)
    {
        if (delay is not { } value || value == Timeout.InfiniteTimeSpan)
        {
            return;
        }

        if (value < TimeSpan.Zero || value.TotalMilliseconds > int.MaxValue)
        {
            throw new ArgumentOutOfRangeException(
                name,
                value,
                $"{name} must be non-negative and within the timer range."
            );
        }
    }

    private static ProcessStartInfo CreateOneShotStartInfo(
        ProcessCommand command,
        ProcessOneShotOptions options
    )
    {
        var startInfo = CreateNonShellStartInfo(command);
        startInfo.RedirectStandardInput = options.StandardInput is not null;
        startInfo.RedirectStandardOutput = true;
        startInfo.RedirectStandardError = true;
        return startInfo;
    }

    private static ProcessStartInfo CreateSessionStartInfo(
        ProcessCommand command,
        ProcessSessionOptions options
    )
    {
        var startInfo = CreateNonShellStartInfo(command);
        startInfo.RedirectStandardInput = options.RedirectStandardInput;
        // Even discarded session output is redirected and continuously drained.
        startInfo.RedirectStandardOutput = true;
        startInfo.RedirectStandardError = true;
        return startInfo;
    }

    private static ProcessStartInfo CreateDetachedStartInfo(ProcessCommand command)
    {
        return CreateNonShellStartInfo(command);
    }

    private static ProcessStartInfo CreateNonShellStartInfo(ProcessCommand command)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(command.FileName);
        var startInfo = new ProcessStartInfo(command.FileName)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in command.Arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        if (command.Environment is not null)
        {
            foreach (var (name, value) in command.Environment)
            {
                startInfo.Environment[name] = value;
            }
        }

        if (command.WorkingDirectory is not null)
        {
            startInfo.WorkingDirectory = command.WorkingDirectory;
        }

        return startInfo;
    }

    // PA32 containment seam: today this is the platform's best-effort tree ownership while
    // the leader lives. A future process-group launcher can replace this method without callers
    // changing; exited-leader/re-parented descendants are deliberately not claimed as contained.
    private static Process? StartContainedProcess(ProcessStartInfo startInfo)
    {
        return Process.Start(startInfo);
    }

    private static Process? StartDetachedProcess(ProcessStartInfo startInfo)
    {
        return Process.Start(startInfo);
    }

    // Shared PA33 cleanup hook. M2 routes expected timeout/cancellation/termination paths here;
    // it deliberately does not broaden the legacy policy for unexpected post-start failures.
    private static async Task TerminateAndReapAsync(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // Best effort while PA32 retains the platform tree-kill strategy.
        }

        using var reapCts = new CancellationTokenSource(s_cleanupGrace);
        try
        {
            await process.WaitForExitAsync(reapCts.Token).ConfigureAwait(false);
        }
        catch
        {
            // Bounded best-effort reaping must not replace the caller's result/exception.
        }
    }

    private static async Task WriteInputAsync(
        Process process,
        ProcessInput? input,
        CancellationToken cancellationToken
    )
    {
        if (input is null)
        {
            return;
        }

        try
        {
            switch (input)
            {
                case Utf8ProcessInput text:
                    await process.StandardInput.WriteAsync(
                            text.Value.AsMemory(),
                            cancellationToken
                        )
                        .ConfigureAwait(false);
                    break;
                case BinaryProcessInput binary:
                    await process.StandardInput.BaseStream.WriteAsync(
                            binary.Value,
                            cancellationToken
                        )
                        .ConfigureAwait(false);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(input));
            }

            await process.StandardInput.FlushAsync(cancellationToken).ConfigureAwait(false);
            process.StandardInput.Close();
        }
        catch (IOException)
        {
            // A process may deliberately close stdin and keep running; exit code and
            // captured output are still the caller's answer, and the deadline still governs.
        }
        catch (ObjectDisposedException)
        {
            // The child closed its end while exiting.
        }
    }

    private static async Task PumpAsync(Stream source, CapturedPipe destination)
    {
        var buffer = new byte[16 * 1024];
        try
        {
            while (true)
            {
                var read = await source.ReadAsync(buffer).ConfigureAwait(false);
                if (read == 0)
                {
                    return;
                }

                destination.Append(buffer.AsSpan(0, read));
            }
        }
        catch (IOException)
        {
            // Closing an abandoned redirected pipe completes its pump.
        }
        catch (ObjectDisposedException)
        {
            // Closing an abandoned redirected pipe completes its pump.
        }
    }

    private static async Task AbandonPumpsAsync(
        Process process,
        Task stdoutTask,
        Task stderrTask
    )
    {
        CloseSafely(process.StandardOutput.BaseStream);
        CloseSafely(process.StandardError.BaseStream);
        try
        {
            await Task.WhenAll(stdoutTask, stderrTask)
                .WaitAsync(s_cleanupGrace)
                .ConfigureAwait(false);
        }
        catch
        {
            ObserveFault(stdoutTask);
            ObserveFault(stderrTask);
        }
    }

    private static void CloseSafely(Stream stream)
    {
        try
        {
            stream.Close();
        }
        catch
        {
            // Best effort abandonment.
        }
    }

    private static void ObserveFault(Task task)
    {
        _ = task.ContinueWith(
            static completed => _ = completed.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default
        );
    }

    private static void ObserveDetachedLauncher(Process process)
    {
        _ = ObserveDetachedLauncherAsync(process);
    }

    private static async Task ObserveDetachedLauncherAsync(Process process)
    {
        using (process)
        {
            try
            {
                await process.WaitForExitAsync().ConfigureAwait(false);
            }
            catch
            {
                // Launch acceptance is already reported; background observation is best effort.
            }
        }
    }

    private static ProcessRunOutcome StartFailed(string error)
    {
        return new ProcessRunOutcome(
            ProcessRunStatus.StartFailed,
            null,
            [],
            [],
            ProcessOutputStatus.Complete,
            error
        );
    }

    private static ProcessRunOutcome TimedOut(CapturedPipe stdout, CapturedPipe stderr)
    {
        return new ProcessRunOutcome(
            ProcessRunStatus.TimedOut,
            null,
            stdout.Snapshot(),
            stderr.Snapshot(),
            ProcessOutputStatus.Complete,
            null
        );
    }

    private static ProcessRunOutcome Exited(
        int exitCode,
        CapturedPipe stdout,
        CapturedPipe stderr,
        ProcessOutputStatus outputStatus
    )
    {
        return new ProcessRunOutcome(
            ProcessRunStatus.Exited,
            exitCode,
            stdout.Snapshot(),
            stderr.Snapshot(),
            outputStatus,
            null
        );
    }

    private sealed class CapturedPipe(ProcessCaptureMode captureMode)
    {
        private readonly Lock _lock = new();
        private readonly MemoryStream? _capture =
            captureMode == ProcessCaptureMode.Discard ? null : new MemoryStream();

        public void Append(ReadOnlySpan<byte> value)
        {
            // ReSharper disable once InconsistentlySynchronizedField -- readonly reference set
            // once in the initializer; the lock guards the stream's contents, not the field.
            if (_capture is null)
            {
                return;
            }

            lock (_lock)
            {
                _capture.Write(value);
            }
        }

        public byte[] Snapshot()
        {
            // ReSharper disable once InconsistentlySynchronizedField -- readonly reference set
            // once in the initializer; the lock guards the stream's contents, not the field.
            if (_capture is null)
            {
                return [];
            }

            lock (_lock)
            {
                return _capture.ToArray();
            }
        }
    }

    private sealed class ProcessSession : IPluginProcessSession
    {
        private readonly Lock _lock = new();
        private readonly Process _process;
        private readonly Channel<ProcessOutputLine> _output =
            Channel.CreateUnbounded<ProcessOutputLine>(
                new UnboundedChannelOptions
                {
                    SingleReader = false,
                    SingleWriter = false,
                    AllowSynchronousContinuations = false,
                }
            );
        private readonly Task _stdoutPump;
        private readonly Task _stderrPump;
        private bool _terminationRequested;
        private Task? _terminationTask;
        private int _disposed;

        public ProcessSession(Process process, ProcessSessionOptions options)
        {
            _process = process;
            ProcessId = process.Id;
            _stdoutPump = PumpSessionOutputAsync(
                process.StandardOutput,
                ProcessStream.StandardOutput,
                options.StandardOutput
            );
            _stderrPump = PumpSessionOutputAsync(
                process.StandardError,
                ProcessStream.StandardError,
                options.StandardError
            );
            Completion = ObserveCompletionAsync();
        }

        public int ProcessId { get; }

        public bool IsRunning => !Completion.IsCompleted;

        public Task<ProcessExitOutcome> Completion { get; }

        public async IAsyncEnumerable<ProcessOutputLine> ReadOutputAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken = default
        )
        {
            await foreach (
                var line in _output.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false)
            )
            {
                yield return line;
            }
        }

        public async ValueTask WriteStandardInputAsync(
            ReadOnlyMemory<byte> data,
            CancellationToken cancellationToken = default
        )
        {
            await _process.StandardInput.BaseStream.WriteAsync(data, cancellationToken)
                .ConfigureAwait(false);
            await _process.StandardInput.BaseStream.FlushAsync(cancellationToken)
                .ConfigureAwait(false);
        }

        public ValueTask CompleteStandardInputAsync(
            CancellationToken cancellationToken = default
        )
        {
            cancellationToken.ThrowIfCancellationRequested();
            _process.StandardInput.Close();
            return ValueTask.CompletedTask;
        }

        public void Terminate()
        {
            _ = EnsureTerminationTask();
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            _ = EnsureTerminationTask();
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                await EnsureTerminationTask().ConfigureAwait(false);
            }

            await Completion.ConfigureAwait(false);
        }

        private Task EnsureTerminationTask()
        {
            lock (_lock)
            {
                _terminationRequested = true;
                return _terminationTask ??= TerminateAndReapAsync(_process);
            }
        }

        private async Task<ProcessExitOutcome> ObserveCompletionAsync()
        {
            try
            {
                await _process.WaitForExitAsync().ConfigureAwait(false);
                var exitCode = _process.ExitCode;
                await DrainPumpsAsync().ConfigureAwait(false);
                lock (_lock)
                {
                    return new ProcessExitOutcome(
                        _terminationRequested
                            ? ProcessExitReason.Terminated
                            : ProcessExitReason.Exited,
                        exitCode
                    );
                }
            }
            finally
            {
                _output.Writer.TryComplete();
                _process.Dispose();
            }
        }

        // A descendant can inherit the redirected pipes and hold them open long after the
        // leader exits, so draining is bounded: Completion — and the DisposeAsync that awaits
        // it — must always reach a terminal state, even when the pipes never see EOF.
        private async Task DrainPumpsAsync()
        {
            try
            {
                await Task.WhenAll(_stdoutPump, _stderrPump)
                    .WaitAsync(s_cleanupGrace)
                    .ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                await AbandonPumpsAsync(_process, _stdoutPump, _stderrPump)
                    .ConfigureAwait(false);
            }
        }

        private async Task PumpSessionOutputAsync(
            StreamReader reader,
            ProcessStream stream,
            ProcessSessionOutputMode mode
        )
        {
            try
            {
                if (mode == ProcessSessionOutputMode.Discard)
                {
                    await reader.BaseStream.CopyToAsync(Stream.Null)
                        .ConfigureAwait(false);
                    return;
                }

                while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
                {
                    await _output.Writer.WriteAsync(new ProcessOutputLine(stream, line))
                        .ConfigureAwait(false);
                }
            }
            catch (IOException)
            {
                // Termination can close a redirected stream while its pump is reading.
            }
            catch (ObjectDisposedException)
            {
                // Termination can close a redirected stream while its pump is reading.
            }
        }
    }
}
