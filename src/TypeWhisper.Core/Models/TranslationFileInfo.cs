namespace TypeWhisper.Core.Models;

/// <summary>One downloadable file of a translation model: its name, source URL, and approximate size in megabytes.</summary>
public sealed record TranslationFileInfo(string FileName, string DownloadUrl, int EstimatedSizeMb);
