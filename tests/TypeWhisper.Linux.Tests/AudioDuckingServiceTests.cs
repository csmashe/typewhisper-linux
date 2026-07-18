using TypeWhisper.Linux.Services;
using Xunit;

namespace TypeWhisper.Linux.Tests;

public sealed class AudioDuckingServiceTests
{
    [Fact]
    public void DuckAndRestore_preserve_channel_vectors_for_each_sink_input()
    {
        const string listing = """
            Sink Input #593
                Volume: front-left: 45875 / 70% / -9.30 dB,   front-right: 26214 / 40% / -23.88 dB
                Base Volume: 65536 / 100% / 0.00 dB
            Sink Input #42
                Volume: mono: 32769 / 50% / -18.06 dB
            """;
        var runner = new FakeProcessRunner();
        runner.RespondWith(
            (fileName, args) =>
                fileName == "pactl" && args.SequenceEqual(["list", "sink-inputs"]),
            listing
        );
        var service = new AudioDuckingService(runner);

        service.DuckAudio(0.5f);
        service.RestoreAudio();

        Assert.Equal(5, runner.Invocations.Count);
        Assert.All(runner.Invocations, invocation => Assert.Equal("pactl", invocation.FileName));
        Assert.All(
            runner.Invocations,
            invocation => Assert.Equal(TimeSpan.FromMilliseconds(1500), invocation.Timeout)
        );
        Assert.Equal(["list", "sink-inputs"], runner.Invocations[0].Args);
        Assert.Equal(
            ["set-sink-input-volume", "593", "22938", "13107"],
            runner.Invocations[1].Args
        );
        Assert.Equal(
            ["set-sink-input-volume", "42", "16385"],
            runner.Invocations[2].Args
        );
        Assert.Equal(
            ["set-sink-input-volume", "593", "45875", "26214"],
            runner.Invocations[3].Args
        );
        Assert.Equal(
            ["set-sink-input-volume", "42", "32769"],
            runner.Invocations[4].Args
        );

        var stereoInvocations = runner.Invocations
            .Where(invocation => invocation.Args.Count > 1 && invocation.Args[1] == "593")
            .ToArray();
        Assert.Equal(2, stereoInvocations.Length);
        Assert.DoesNotContain(
            stereoInvocations,
            invocation =>
                invocation.Args.SequenceEqual(["set-sink-input-volume", "593", "70%"]) ||
                invocation.Args.SequenceEqual(["set-sink-input-volume", "593", "45875"])
        );
    }
}
