using System.Reflection;
using TypeWhisper.Linux;

namespace TypeWhisper.Integration.Tests;

internal static class PrivateAppLifecycleInvoker
{
    internal static Task TearDownAsync(IServiceProvider provider)
    {
        var method = typeof(App).GetMethod(
            "TearDownAsync",
            BindingFlags.Static | BindingFlags.NonPublic,
            binder: null,
            types: [typeof(IServiceProvider)],
            modifiers: null
        ) ?? throw new MissingMethodException(
            typeof(App).FullName,
            "TearDownAsync(IServiceProvider)"
        );

        try
        {
            return method.Invoke(null, [provider]) as Task
                ?? throw new InvalidOperationException(
                    "App.TearDownAsync no longer returned a Task. Update the lifecycle test deliberately."
                );
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            throw new InvalidOperationException(
                "App.TearDownAsync failed before returning its teardown task.",
                ex.InnerException
            );
        }
    }
}
