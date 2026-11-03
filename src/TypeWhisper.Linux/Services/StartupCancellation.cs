namespace TypeWhisper.Linux.Services;

/// <summary>
///     Shared exit path for startups that cannot establish sole ownership of the control
///     socket. No window ever maps on these paths, so each must also clear the launcher's
///     busy cursor.
/// </summary>
internal static class StartupCancellation
{
    internal static void NotifyUnverifiedInstance()
    {
        Console.Error.WriteLine(
            "TypeWhisper could not verify that no other instance is running. Startup was canceled."
        );
        LinuxStartupNotification.NotifyComplete();
    }
}
