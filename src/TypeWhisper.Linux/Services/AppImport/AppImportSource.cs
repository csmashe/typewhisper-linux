using TypeWhisper.Core.Services.AppImport;

namespace TypeWhisper.Linux.Services.AppImport;

/// <summary>
///     Another dictation app whose dictionary can be imported: how to read it and which source-specific messages
///     explain a file that is not its data or is over its size limit.
/// </summary>
public sealed record AppImportSource(
    string AppName,
    Func<string, CancellationToken, AppImportBatch> Load,
    string MalformedKey,
    string TooLargeKey
)
{
    /// <summary>Reads Handy's <c>settings_store.json</c>, discovered or picked by the user.</summary>
    /// <param name="read">Test seam that replaces the bounded settings-file read.</param>
    public static AppImportSource Handy(Func<string, CancellationToken, byte[]>? read = null) =>
        new("Handy", (path, token) => HandySettingsReader.Load(path, token, read), "HandyImport.Malformed", "HandyImport.TooLarge");

    /// <summary>Reads a <c>flow.sqlite</c> the user picks; Wispr Flow has no Linux install to discover.</summary>
    public static AppImportSource WisprFlow { get; } =
        new("Wispr Flow", WisprFlowImport.Load, "WisprImport.Malformed", "WisprImport.TooLarge");

    /// <summary>
    ///     Where Handy keeps its settings on Linux: Tauri's app data directory,
    ///     <c>$XDG_DATA_HOME/com.pais.handy</c>. Portable installs keep a <c>Data</c> folder beside
    ///     Handy's executable instead, which cannot be discovered and has to be picked by hand.
    /// </summary>
    internal static string HandyDefaultSettingsPath() =>
        Path.Join(XdgPaths.ResolveDataHome(), HandySettingsReader.AppIdentifier, HandySettingsReader.SettingsFileName);
}
