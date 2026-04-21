using System;
using System.IO;
using Avalonia;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using TypeWhisper.Core;

namespace TypeWhisper.Linux;

public static class Program
{
    private static FileStream? _singleInstanceLock;

    public static IHost Host { get; private set; } = null!;
    public static bool StartMinimized { get; private set; }

    public static int Main(string[] args)
    {
        StartMinimized = args.Contains("--minimized", StringComparer.OrdinalIgnoreCase);
        TypeWhisperEnvironment.EnsureDirectories();

        if (!AcquireSingleInstanceLock())
        {
            Console.Error.WriteLine("TypeWhisper is already running.");
            return 0;
        }

        try
        {
            Host = BuildHost(args);
            Host.Start();

            return BuildAvaloniaApp()
                .StartWithClassicDesktopLifetime(args);
        }
        finally
        {
            Host?.StopAsync().GetAwaiter().GetResult();
            _singleInstanceLock?.Dispose();
        }
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
#if DEBUG
            .WithDeveloperTools()
#endif
            .WithInterFont()
            .LogToTrace();

    private static IHost BuildHost(string[] args)
        => Microsoft.Extensions.Hosting.Host.CreateDefaultBuilder(args)
            .ConfigureServices(ServiceRegistrations.Register)
            .Build();

    private static bool AcquireSingleInstanceLock()
    {
        var runtimeDir = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR")
            ?? Path.Combine(Path.GetTempPath(), $"typewhisper-{Environment.UserName}");
        Directory.CreateDirectory(runtimeDir);

        var lockPath = Path.Combine(runtimeDir, "typewhisper.lock");
        try
        {
            _singleInstanceLock = new FileStream(
                lockPath,
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.None);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
    }
}
