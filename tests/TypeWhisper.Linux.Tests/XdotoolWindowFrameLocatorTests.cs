using TypeWhisper.Linux.Services;
using TypeWhisper.Linux.Services.ActiveWindow;
using Xunit;

namespace TypeWhisper.Linux.Tests;

public sealed class XdotoolWindowFrameLocatorTests
{
    [Fact]
    public async Task ReadsTheActiveWindowAndItsRootGeometry()
    {
        var runner = new ScriptedProcessRunner(args => args[0] switch
        {
            "getactivewindow" => "6291459\n",
            _ => "WINDOW=6291459\nX=701\nY=474\nWIDTH=440\nHEIGHT=300\nSCREEN=0\n",
        });
        var locator = new XdotoolWindowFrameLocator(runner, applicable: true);

        var handle = await locator.TryGetFocusedWindowHandleAsync(CancellationToken.None);
        var frame = await locator.TryGetWindowFrameAsync(handle!, CancellationToken.None);

        Assert.Equal("6291459", handle);
        Assert.Equal(new CompositorWindowFrame(new AtSpiScreenRect(701, 474, 440, 300), null), frame);
        Assert.Equal(["getwindowgeometry", "--shell", "6291459"], runner.Calls[1]);
    }

    [Fact]
    public void ParsesNegativeOrigins()
    {
        Assert.Equal(
            new AtSpiScreenRect(-1200, -40, 800, 600),
            XdotoolWindowFrameLocator.ParseWindowGeometry("X=-1200\nY=-40\nWIDTH=800\nHEIGHT=600\n")
        );
    }

    [Theory]
    [InlineData("X=10\nY=20\nWIDTH=0\nHEIGHT=300")]
    [InlineData("X=10\nY=20\nWIDTH=440")]
    [InlineData("XGetWindowAttributes failed")]
    public void RejectsMalformedGeometry(string output)
    {
        Assert.Null(XdotoolWindowFrameLocator.ParseWindowGeometry(output));
    }

    [Fact]
    public async Task InertOutsideX11()
    {
        var runner = new ScriptedProcessRunner(_ => throw new InvalidOperationException());
        var locator = new XdotoolWindowFrameLocator(runner, applicable: false);

        Assert.Null(await locator.TryGetFocusedWindowHandleAsync(CancellationToken.None));
        Assert.Null(await locator.TryGetWindowFrameAsync("42", CancellationToken.None));
        Assert.Empty(runner.Calls);
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
