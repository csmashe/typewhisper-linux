using TypeWhisper.Core.Models;

namespace TypeWhisper.Core.Interfaces;

/// <summary>
///     In-memory ring buffer of recent error and diagnostic messages, surfaced in the
///     About screen and exportable for bug reports.
/// </summary>
public interface IErrorLogService
{
    IReadOnlyList<ErrorLogEntry> Entries { get; }

    /// <summary>Appends a message under the given <paramref name="category" />, evicting the oldest entry once full.</summary>
    void AddEntry(string message, string category = ErrorCategory.General);

    void ClearAll();

    /// <summary>Renders all entries as a plain-text diagnostics block for sharing in bug reports.</summary>
    string ExportDiagnostics();

    event Action? EntriesChanged;
}
