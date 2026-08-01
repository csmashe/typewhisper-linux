using System.Security.Cryptography;
using System.Text;

namespace TypeWhisper.ProcessTestChild;

public static class ProcessTestChildMarker;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        if (args.Length == 0)
        {
            return 2;
        }

        return args[0] switch
        {
            "pressure" => await PressureAsync(int.Parse(args[1])),
            "read-stdin" => await ReadStdinAsync(),
            "flood" => await FloodAsync(int.Parse(args[1])),
            "wait" => await WaitAsync(args[1]),
            "spawn-child" => await SpawnChildAsync(args[1]),
            "delay-exit" => await DelayExitAsync(int.Parse(args[1]), int.Parse(args[2])),
            "monitor-lines" => await MonitorLinesAsync(
                int.Parse(args[1]),
                int.Parse(args[2])
            ),
            "hold-pipes-after-exit" => HoldPipesAfterExit(
                args[1],
                int.Parse(args[2])
            ),
            "detached-marker" => await DetachedMarkerAsync(
                args[1],
                int.Parse(args[2])
            ),
            _ => 2,
        };
    }

    private static async Task<int> PressureAsync(int bytes)
    {
        var payload = Enumerable.Repeat((byte)'x', bytes).ToArray();
        await Task.WhenAll(
            Console.OpenStandardOutput().WriteAsync(payload).AsTask(),
            Console.OpenStandardError().WriteAsync(payload).AsTask()
        );
        await ReadStdinAsync();
        return 0;
    }

    private static async Task<int> ReadStdinAsync()
    {
        using var input = new MemoryStream();
        await Console.OpenStandardInput().CopyToAsync(input);
        var hash = Convert.ToHexString(SHA256.HashData(input.ToArray()));
        await Console.Out.WriteLineAsync($"{input.Length}:{hash}");
        return 0;
    }

    private static async Task<int> FloodAsync(int bytes)
    {
        var stdout = Enumerable.Repeat((byte)'o', bytes).ToArray();
        var stderr = Enumerable.Repeat((byte)'e', bytes).ToArray();
        await Task.WhenAll(
            Console.OpenStandardOutput().WriteAsync(stdout).AsTask(),
            Console.OpenStandardError().WriteAsync(stderr).AsTask()
        );
        return 0;
    }

    private static async Task<int> WaitAsync(string pidPath)
    {
        await File.WriteAllTextAsync(pidPath, Environment.ProcessId.ToString());
        await Task.Delay(Timeout.InfiniteTimeSpan);
        return 0;
    }

    private static async Task<int> SpawnChildAsync(string pidPath)
    {
        var childPath = $"{pidPath}.child";
        var startInfo = CreateSelfStartInfo(["wait", childPath]);
        var child = System.Diagnostics.Process.Start(startInfo)!;
        await File.WriteAllTextAsync(
            pidPath,
            $"{Environment.ProcessId}:{child.Id}"
        );
        await Task.Delay(Timeout.InfiniteTimeSpan);
        return 0;
    }

    private static async Task<int> DelayExitAsync(int milliseconds, int exitCode)
    {
        await Task.Delay(milliseconds);
        return exitCode;
    }

    private static async Task<int> MonitorLinesAsync(int count, int delayMilliseconds)
    {
        for (var index = 0; index < count; index++)
        {
            await Console.Out.WriteLineAsync($"out-{index}");
            await Console.Error.WriteLineAsync($"err-{index}");
            await Task.Delay(delayMilliseconds);
        }

        return 0;
    }

    private static int HoldPipesAfterExit(string pidPath, int delayMilliseconds)
    {
        var startInfo = CreateSelfStartInfo(
            ["delay-exit", delayMilliseconds.ToString(), "0"]
        );
        startInfo.RedirectStandardOutput = false;
        startInfo.RedirectStandardError = false;
        var child = System.Diagnostics.Process.Start(startInfo)!;
        File.WriteAllText(pidPath, child.Id.ToString());
        return 0;
    }

    private static async Task<int> DetachedMarkerAsync(string path, int delayMilliseconds)
    {
        await Task.Delay(delayMilliseconds);
        await File.WriteAllTextAsync(
            path,
            $"{Environment.ProcessId}:{Encoding.UTF8.GetString("continued"u8)}"
        );
        return 0;
    }

    private static System.Diagnostics.ProcessStartInfo CreateSelfStartInfo(
        IReadOnlyList<string> arguments
    )
    {
        var processPath = Environment.ProcessPath
                          ?? throw new InvalidOperationException("No process path.");
        var startInfo = new System.Diagnostics.ProcessStartInfo(processPath)
        {
            UseShellExecute = false,
        };
        if (string.Equals(Path.GetFileName(processPath), "dotnet", StringComparison.Ordinal))
        {
            startInfo.ArgumentList.Add(typeof(ProcessTestChildMarker).Assembly.Location);
        }

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        return startInfo;
    }
}
