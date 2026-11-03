using TypeWhisper.Linux.Services;
using TypeWhisper.Linux.Services.ActiveWindow;
using Xunit;

namespace TypeWhisper.Linux.Tests;

public sealed class GnomeWindowFrameLocatorTests
{
    [Fact]
    public void ParsesWindowCallsFrameRect()
    {
        const string output = """('{"x":1510,"y":39,"width":820,"height":547}',)""";
        Assert.Equal(
            new AtSpiScreenRect(1510, 39, 820, 547),
            GnomeWindowFrameLocator.ParseFrameRect(output)
        );
    }

    [Theory]
    [InlineData("""('{"x":10,"y":20,"width":0,"height":547}',)""")]
    [InlineData("""('{"x":10,"y":20,"width":820}',)""")]
    [InlineData("('[]',)")]
    [InlineData("('not json',)")]
    [InlineData("Error: GDBus.Error:org.freedesktop.DBus.Error.UnknownMethod")]
    public void RejectsMalformedOrEmptyFrames(string output)
    {
        Assert.Null(GnomeWindowFrameLocator.ParseFrameRect(output));
    }

    [Fact]
    public async Task ReadsFocusedWindowIdAndItsFrameThroughWindowCalls()
    {
        var runner = new ScriptedProcessRunner(args => args[^1] switch
        {
            "org.gnome.Shell.Extensions.Windows.List" =>
                """('[{"id":7,"has_focus":false},{"id":153140036,"has_focus":true}]',)""",
            _ => """('{"x":1510,"y":39,"width":820,"height":547}',)""",
        });
        var desktop = new AtSpiScreenRect(0, 0, 2560, 800);
        var locator = new GnomeWindowFrameLocator(runner, applicable: true, _ => Task.FromResult<AtSpiScreenRect?>(desktop));

        var handle = await locator.TryGetFocusedWindowHandleAsync(CancellationToken.None);
        var frame = await locator.TryGetWindowFrameAsync(handle!, CancellationToken.None);

        Assert.Equal("153140036", handle);
        Assert.Equal(new CompositorWindowFrame(new AtSpiScreenRect(1510, 39, 820, 547), desktop), frame);
        Assert.Equal(
            ["org.gnome.Shell.Extensions.Windows.GetFrameRect", "uint32 153140036"],
            runner.Calls[1][^2..]
        );
    }

    [Fact]
    public async Task InertOutsideGnomeWayland()
    {
        var runner = new ScriptedProcessRunner(_ => throw new InvalidOperationException());
        var locator = new GnomeWindowFrameLocator(
            runner,
            applicable: false,
            _ => throw new InvalidOperationException()
        );

        Assert.Null(await locator.TryGetFocusedWindowHandleAsync(CancellationToken.None));
        Assert.Null(await locator.TryGetWindowFrameAsync("42", CancellationToken.None));
        Assert.Empty(runner.Calls);
    }

    [Fact]
    public async Task NoFrameWhenTheLogicalDesktopIsUnknown()
    {
        var runner = new ScriptedProcessRunner(_ => """('{"x":1510,"y":39,"width":820,"height":547}',)""");
        var locator = new GnomeWindowFrameLocator(runner, applicable: true, _ => Task.FromResult<AtSpiScreenRect?>(null));

        Assert.Null(await locator.TryGetWindowFrameAsync("42", CancellationToken.None));
    }

    [Fact]
    public void LogicalDesktopDividesFractionalMonitorsByTheirScale()
    {
        // 1280x800 at 100% plus 1600x900 at 125% (logical 1280x720) to its right.
        var desktop = MutterDisplayConfig.ComputeDesktop(
            [
                new MutterDisplayConfig.LogicalMonitor(0, 0, 1.0, 0, 1280, 800),
                new MutterDisplayConfig.LogicalMonitor(1280, 0, 1.25, 0, 1600, 900),
            ],
            logicalLayout: true
        );

        Assert.Equal(new AtSpiScreenRect(0, 0, 2560, 800), desktop);
    }

    [Fact]
    public void PhysicalLayoutKeepsModeSizesAndRotationSwapsThem()
    {
        var desktop = MutterDisplayConfig.ComputeDesktop(
            [
                new MutterDisplayConfig.LogicalMonitor(0, 0, 2.0, 0, 1920, 1080),
                new MutterDisplayConfig.LogicalMonitor(1920, 0, 1.0, 1, 1920, 1080),
            ],
            logicalLayout: false
        );

        Assert.Equal(new AtSpiScreenRect(0, 0, 3000, 1920), desktop);
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
