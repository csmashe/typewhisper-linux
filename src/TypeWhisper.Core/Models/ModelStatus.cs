// ReSharper disable MemberCanBePrivate.Global
namespace TypeWhisper.Core.Models;

/// <summary>
///     Snapshot of a model's lifecycle <see cref="Type" /> plus the extra detail
///     that state carries — download <see cref="Progress" /> and throughput while
///     downloading, or an <see cref="ErrorMessage" /> on failure. The static
///     factories build the common states.
/// </summary>
public sealed record ModelStatus
{
    public required ModelStatusType Type { get; init; }
    public double Progress { get; init; }
    public double? BytesPerSecond { get; init; }
    public string? ErrorMessage { get; init; }

    public static ModelStatus NotDownloaded => new() { Type = ModelStatusType.NotDownloaded };
    public static ModelStatus Ready => new() { Type = ModelStatusType.Ready };
    public static ModelStatus LoadingModel => new() { Type = ModelStatusType.Loading };

    public static ModelStatus DownloadingModel(double progress, double? bytesPerSecond = null)
    {
        return new ModelStatus
        {
            Type = ModelStatusType.Downloading, Progress = progress, BytesPerSecond = bytesPerSecond
        };
    }

    public static ModelStatus Failed(string message)
    {
        return new ModelStatus { Type = ModelStatusType.Error, ErrorMessage = message };
    }
}