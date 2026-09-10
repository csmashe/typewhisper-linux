namespace TypeWhisper.Core.Models;

/// <summary>Which widget occupies an overlay slot (e.g. waveform, timer, clock, profile name) or none.</summary>
public enum OverlayWidget
{
    None,
    Indicator,
    Timer,
    Waveform,
    Clock,
    Profile,
    HotkeyMode,
    AppName,
}
