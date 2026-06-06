namespace TypeWhisper.Cli.Output;

/// <summary>
///     Small console helpers shared by the commands: error reporting (writes to
///     stderr and returns the process exit code) and fixed-width padding for
///     the tabular <c>models</c> listing.
/// </summary>
internal static class ConsoleOutput
{
    public static int Error(string message)
    {
        Console.Error.WriteLine($"Error: {message}");
        return 1;
    }

    public static string Pad(string value, int width)
    {
        return value.PadRight(width);
    }
}