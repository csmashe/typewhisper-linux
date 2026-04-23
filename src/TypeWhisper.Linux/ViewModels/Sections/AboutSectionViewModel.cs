using System.Collections.ObjectModel;
using System.Reflection;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;

namespace TypeWhisper.Linux.ViewModels.Sections;

public partial class AboutSectionViewModel : ObservableObject
{
    private readonly IErrorLogService _errorLog;

    public string Version { get; } =
        Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "dev";

    public string RuntimeVersion { get; } = Environment.Version.ToString();

    public string OsDescription { get; } = System.Runtime.InteropServices.RuntimeInformation.OSDescription;

    public string Architecture { get; } =
        System.Runtime.InteropServices.RuntimeInformation.OSArchitecture.ToString();

    public string ProjectUrl { get; } = "https://github.com/csmashe/typewhisper-linux";

    public string UpstreamUrl { get; } = "https://github.com/TypeWhisper/typewhisper-win";

    public bool CanCheckForUpdates => false;

    public string UpdateStatusText => "Automatic updates are not configured in this Linux build yet.";

    public ObservableCollection<ErrorLogEntry> ErrorEntries { get; } = [];
    public bool HasErrors => ErrorEntries.Count > 0;

    public AboutSectionViewModel(IErrorLogService errorLog)
    {
        _errorLog = errorLog;
        RefreshErrors();
        _errorLog.EntriesChanged += RefreshErrors;
    }

    [RelayCommand]
    private void ClearErrors()
    {
        _errorLog.ClearAll();
        RefreshErrors();
    }

    public string ExportDiagnostics() => _errorLog.ExportDiagnostics();

    private void RefreshErrors()
    {
        ErrorEntries.Clear();
        foreach (var entry in _errorLog.Entries)
            ErrorEntries.Add(entry);

        OnPropertyChanged(nameof(HasErrors));
    }
}
