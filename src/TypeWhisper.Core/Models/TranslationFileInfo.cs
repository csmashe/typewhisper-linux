namespace TypeWhisper.Core.Models;

/// <summary>One downloadable file of a translation model: its name and source URL.</summary>
public sealed record TranslationFileInfo(string FileName, string DownloadUrl);
