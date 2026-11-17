using TypeWhisper.Linux.Services;
using TypeWhisper.Linux.Services.ActiveWindow;
using Xunit;

namespace TypeWhisper.Linux.Tests;

public sealed class XdotoolActiveWindowProviderTests
{
    [Fact]
    public async Task ReadsTitleAndPidFromTheCapturedWindowEvenIfFocusMoves()
    {
        var activeWindow = 0;
        var pid = Environment.ProcessId;
        var runner = new ScriptedProcessRunner(args => args[0] switch
        {
            // Focus moves to another window after every read of the active one.
            "getactivewindow" => $"{++activeWindow}\n",
            "getwindowname" => $"Window {args[1]}\n",
            "getwindowpid" => args[1] == "1" ? $"{pid}\n" : "1\n",
            _ => throw new InvalidOperationException(string.Join(' ', args)),
        });
        var provider = new XdotoolActiveWindowProvider(runner);

        var snapshot = await provider.TryGetActiveWindowAsync(CancellationToken.None);

        Assert.NotNull(snapshot);
        Assert.Equal("1", snapshot.WindowId);
        Assert.Equal("Window 1", snapshot.Title);
        Assert.Equal(pid, snapshot.ProcessId);
        Assert.Equal(
            [["getactivewindow"], ["getwindowname", "1"], ["getwindowpid", "1"]],
            runner.Calls
        );
    }

    private sealed class ScriptedProcessRunner(Func<string[], string> respond) : IProcessRunner
    {
        public List<string[]> Calls { get; } = [];

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
            var argv = args.ToArray();
            Calls.Add(argv);
            return Task.FromResult(new ProcessRunResult(true, false, 0, respond(argv), string.Empty));
        }
    }
}
