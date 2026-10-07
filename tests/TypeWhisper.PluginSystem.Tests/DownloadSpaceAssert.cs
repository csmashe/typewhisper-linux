namespace TypeWhisper.PluginSystem.Tests;

/// <summary>
///     The shared download space policy is file-linked into several plugin
///     assemblies, so its exception type is ambiguous here; match it by name.
/// </summary>
internal static class DownloadSpaceAssert
{
    public static async Task<IOException> ThrowsInsufficientSpaceAsync(Func<Task> action)
    {
        var ex = await Assert.ThrowsAnyAsync<IOException>(action);
        Assert.Equal("InsufficientDownloadSpaceException", ex.GetType().Name);
        Assert.Equal(28, ex.HResult);
        return ex;
    }
}
