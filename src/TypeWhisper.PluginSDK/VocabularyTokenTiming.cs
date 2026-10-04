// Public plugin-SDK surface consumed by plugins and the host.
namespace TypeWhisper.PluginSDK;

/// <summary>A main-recognizer token with times in seconds relative to the start of the supplied audio.</summary>
/// <param name="Text">The decoded token text.</param>
/// <param name="StartSeconds">Inclusive token start.</param>
/// <param name="EndSeconds">Exclusive token end.</param>
// ReSharper disable once UnusedType.Global
public sealed record VocabularyTokenTiming(string Text, double StartSeconds, double EndSeconds);
