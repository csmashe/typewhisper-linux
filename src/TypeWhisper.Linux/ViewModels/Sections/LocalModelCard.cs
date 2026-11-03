using CommunityToolkit.Mvvm.ComponentModel;
using TypeWhisper.Core.Models;

namespace TypeWhisper.Linux.ViewModels.Sections;

/// <summary>One downloadable local model in Dictation settings, with its state and actions.</summary>
public sealed partial class LocalModelCard(
    string modelId,
    string displayName,
    string engineName,
    string? sizeDescription,
    bool isRecommended) : ObservableObject
{
    [ObservableProperty]
    private bool _isListed;

    [ObservableProperty]
    private string _badgeText = "";

    [ObservableProperty]
    private string _stateText = "";

    [ObservableProperty]
    private bool _isActive;

    [ObservableProperty]
    private bool _isFailed;

    [ObservableProperty]
    private bool _showProgress;

    [ObservableProperty]
    private bool _isProgressIndeterminate;

    [ObservableProperty]
    private double _progress;

    [ObservableProperty]
    private string _percentText = "";

    [ObservableProperty]
    private bool _showDownload;

    [ObservableProperty]
    private bool _canDownload;

    [ObservableProperty]
    private bool _showUse;

    [ObservableProperty]
    private bool _canUse;

    [ObservableProperty]
    private string _useText = "";

    [ObservableProperty]
    private bool _showRemove;

    [ObservableProperty]
    private bool _canRemove;

    [ObservableProperty]
    private bool _showCancel;

    public string ModelId { get; } = modelId;
    public string DisplayName { get; } = displayName;
    public bool IsRecommended { get; } = isRecommended;

    public string DetailText { get; } =
        string.IsNullOrWhiteSpace(sizeDescription) ? engineName : $"{sizeDescription} · {engineName}";

    // Cached so progress ticks do not stat every model's files.
    internal bool Downloaded { get; set; }
    internal ModelStatusType? LastStatusType { get; set; }
    internal bool WasBusy { get; set; }
}
