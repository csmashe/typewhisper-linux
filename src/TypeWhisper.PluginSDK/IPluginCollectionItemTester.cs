// Public plugin-SDK surface. The per-item `disable once` directives below mark members
// ReSharper/Qodana cannot see used from this project (they are consumed by external plugins/
// the host). Per-item, not file-level, so a genuinely-unused member added later still surfaces.
namespace TypeWhisper.PluginSDK;

/// <summary>
///     Optional companion to <see cref="IPluginCollectionSettingsProvider" /> for collections
///     whose definition sets <see cref="PluginCollectionDefinition.ItemTest" />. The host passes
///     an item's current, unsaved draft; the plugin must not store it or apply it to live work.
///     The host sets no time limit (the user can cancel), so the plugin bounds its own work.
/// </summary>
/// <remarks>
///     Uses the SDK cancellation-origin contract: caller cancellation throws
///     <see cref="OperationCanceledException" /> only when the supplied token is requested;
///     a failed test is a result with <c>IsSuccess</c> false, not an exception.
/// </remarks>
// ReSharper disable once UnusedType.Global
public interface IPluginCollectionItemTester
{
    /// <summary>Tests <paramref name="draft" /> against <paramref name="input" />.</summary>
    Task<PluginCollectionItemTestResult> TestItemAsync(
        // ReSharper disable UnusedParameter.Global
        string collectionKey,
        PluginCollectionItem draft,
        string input,
        CancellationToken ct
        // ReSharper restore UnusedParameter.Global
    );
}

/// <summary>The outcome of a draft item test.</summary>
/// <param name="IsSuccess">Whether the test passed.</param>
/// <param name="Message">Short status for the user, such as why the test failed.</param>
/// <param name="Output">Optional produced text, shown read-only and selectable.</param>
// ReSharper disable once UnusedType.Global
public sealed record PluginCollectionItemTestResult(
    bool IsSuccess,
    string Message,
    string? Output = null
);
