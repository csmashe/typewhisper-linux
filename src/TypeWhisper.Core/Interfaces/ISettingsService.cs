using TypeWhisper.Core.Models;

namespace TypeWhisper.Core.Interfaces;

public interface ISettingsService
{
    AppSettings Current { get; }
    AppSettings Load();
    void Save(AppSettings settings);
    event Action<AppSettings>? SettingsChanged;
}