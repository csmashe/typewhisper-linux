namespace TypeWhisper.Linux.Services.ManagedArtifacts;

public enum ManagedFileClassification
{
    Absent,
    CurrentOwned,
    StaleOwned,
    CustomizedOwned,
    EquivalentForeign,
    Foreign,
    UnsupportedEntry,
}

public sealed record ManagedFileOperationResult(
    ManagedFileClassification Classification,
    bool Changed,
    bool OwnsDestination,
    string? Detail = null
);

// State and journal are persisted to and rehydrated from JSON by System.Text.Json (reflection),
// so `init` must stay writable and the getters are read by the serializer, not by visible code.
// ReSharper disable AutoPropertyCanBeMadeGetOnly.Global
// ReSharper disable UnusedAutoPropertyAccessor.Global
internal sealed record ManagedFileState
{
    public int Version { get; init; } = 1;
    public required string ArtifactId { get; init; }
    public required string DestinationPath { get; init; }
    public required string PublishedSha256 { get; init; }
    public required int PublishedMode { get; init; }
    public string? PreimageSha256 { get; init; }
    public int? PreimageMode { get; init; }
}

internal enum ManagedFileJournalOperation
{
    Install,
    Remove,
}

internal sealed record ManagedFileJournal
{
    public int Version { get; init; } = 1;
    public required ManagedFileJournalOperation Operation { get; init; }
    public required string ArtifactId { get; init; }
    public required string DestinationPath { get; init; }
    public required bool OldExists { get; init; }
    public int? OldMode { get; init; }
    public required bool NewExists { get; init; }
    public int? NewMode { get; init; }
    public string? FinalPreimageSha256 { get; init; }
    public int? FinalPreimageMode { get; init; }
    public bool JournalCarriesPreimage { get; init; }
}

// ReSharper restore UnusedAutoPropertyAccessor.Global
// ReSharper restore AutoPropertyCanBeMadeGetOnly.Global

internal enum ManagedFileCheckpoint
{
    InstallAfterStage,
    InstallAfterJournal,
    InstallAfterPublish,
    InstallAfterState,
    RemoveAfterJournal,
    RemoveAfterPublish,
    RemoveAfterState,
}
