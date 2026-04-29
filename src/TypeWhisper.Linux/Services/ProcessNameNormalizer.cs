namespace TypeWhisper.Linux.Services;

internal static class ProcessNameNormalizer
{
    public static string Normalize(string? processName)
    {
        if (string.IsNullOrWhiteSpace(processName))
            return "";

        var baseName = Path.GetFileName(processName.Trim());
        return baseName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? Path.GetFileNameWithoutExtension(baseName)
            : baseName;
    }
}
