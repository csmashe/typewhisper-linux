using TypeWhisper.Core.Models;

namespace TypeWhisper.Core.Interfaces;

public interface IErrorLogService
{
    IReadOnlyList<ErrorLogEntry> Entries { get; }

    void AddEntry(string message, string category = "general");
    void ClearAll();
    string ExportDiagnostics();
    event Action? EntriesChanged;
}