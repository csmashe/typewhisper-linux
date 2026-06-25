using System.Diagnostics;
using System.Text.Json;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;

namespace TypeWhisper.Core.Services;

/// <summary>
///     File-backed <see cref="IPromptActionService" />: persists the user's LLM prompt actions as
///     JSON and seeds the built-in presets and first-run defaults.
/// </summary>
public sealed class PromptActionService : IPromptActionService
{
    private static readonly JsonSerializerOptions s_jsonOptions = new() { WriteIndented = true };

    private readonly string _filePath;
    private readonly IErrorLogService? _errorLog;
    private List<PromptAction> _cache = [];
    private bool _cacheLoaded;

    public PromptActionService(string filePath, IErrorLogService? errorLog = null)
    {
        _filePath = filePath;
        _errorLog = errorLog;
    }

    public IReadOnlyList<PromptAction> Actions
    {
        get
        {
            EnsureCacheLoaded();
            return _cache;
        }
    }

    public IReadOnlyList<PromptAction> EnabledActions
    {
        get
        {
            EnsureCacheLoaded();
            return _cache.Where(a => a.IsEnabled).OrderBy(a => a.SortOrder).ToList();
        }
    }

    public event Action? ActionsChanged;

    public void AddAction(PromptAction action)
    {
        EnsureCacheLoaded();
        _cache.Add(action);
        SaveToDisk();
        ActionsChanged?.Invoke();
    }

    public void UpdateAction(PromptAction action)
    {
        EnsureCacheLoaded();
        var updated = action with { UpdatedAt = DateTime.UtcNow };
        var idx = _cache.FindIndex(a => a.Id == action.Id);
        if (idx >= 0)
        {
            _cache[idx] = updated;
        }

        SaveToDisk();
        ActionsChanged?.Invoke();
    }

    public void DeleteAction(string id)
    {
        EnsureCacheLoaded();
        _cache.RemoveAll(a => a.Id == id);
        SaveToDisk();
        ActionsChanged?.Invoke();
    }

    public void Reorder(IReadOnlyList<string> orderedIds)
    {
        EnsureCacheLoaded();
        for (var i = 0; i < orderedIds.Count; i++)
        {
            var idx = _cache.FindIndex(a => a.Id == orderedIds[i]);
            if (idx >= 0)
            {
                _cache[idx] = _cache[idx] with { SortOrder = i };
            }
        }

        SaveToDisk();
        ActionsChanged?.Invoke();
    }

    public void SeedFirstRunDefaultsIfMissing()
    {
        // Seed only on a genuine first run — when the actions file has never
        // been written. If the user later disables or deletes the seeded
        // action the file still exists, so we never resurrect it.
        if (File.Exists(_filePath))
        {
            return;
        }

        EnsureCacheLoaded();
        if (_cache.Any(a => a.Id == FirstRunDefaults.AutoCleanupActionId))
        {
            return;
        }

        _cache.Add(FirstRunDefaults.CreateAutoCleanupAction());
        SaveToDisk();
        ActionsChanged?.Invoke();
    }

    public void SeedPresets()
    {
        EnsureCacheLoaded();
        if (_cache.Any(a => a.IsPreset))
        {
            return;
        }

        var presets = new (string Name, string Icon, string Prompt)[]
        {
            (
                "Translate to English",
                "\U0001F30D",
                "Translate the following text to English. Return only the translated text, no explanations."
            ),
            (
                "Write Email",
                "\u2709\uFE0F",
                "Rewrite the following text as a professional email. Keep the same meaning and tone but make it polished and suitable for business communication. Return only the email body."
            ),
            (
                "Format as List",
                "\U0001F4CB",
                "Convert the following text into a clean bullet-point list. Return only the formatted list."
            ),
            (
                "Action Items",
                "\u2705",
                "Extract all action items and tasks from the following text. Return them as a numbered list. If no action items are found, say so briefly."
            ),
            (
                "Reply",
                "\U0001F4AC",
                "Write a concise, professional reply to the following message. Match the tone of the original. Return only the reply text."
            )
        };

        for (var i = 0; i < presets.Length; i++)
        {
            var (name, icon, prompt) = presets[i];
            _cache.Add(
                new PromptAction
                {
                    Id = Guid.NewGuid().ToString(),
                    Name = name,
                    SystemPrompt = prompt,
                    Icon = icon,
                    IsPreset = true,
                    SortOrder = i
                }
            );
        }

        SaveToDisk();
        ActionsChanged?.Invoke();
    }

    private void EnsureCacheLoaded()
    {
        if (_cacheLoaded)
        {
            return;
        }

        try
        {
            if (File.Exists(_filePath))
            {
                var json = File.ReadAllText(_filePath);
                _cache = JsonSerializer.Deserialize<List<PromptAction>>(json) ?? [];
            }
        }
        catch (Exception ex)
        {
            // The user's saved prompt actions are unreadable — they'll see only the
            // built-in presets until the file is repaired, so make that visible.
            _errorLog?.AddEntry(
                $"Could not load saved prompt actions from {_filePath}: {ex.Message}",
                ErrorCategory.Prompt
            );
            _cache = [];
        }

        _cacheLoaded = true;
    }

    private void SaveToDisk()
    {
        try
        {
            var dir = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            var json = JsonSerializer.Serialize(_cache, s_jsonOptions);
            File.WriteAllText(_filePath, json);
        }
        catch (Exception ex)
        {
            // Best-effort persistence: don't crash the caller, but log so a failing save is visible.
            Trace.WriteLine($"[PromptActionService] Failed to save prompt actions to {_filePath}: {ex}");
            _errorLog?.AddEntry(
                $"Could not save prompt actions to {_filePath}: {ex.Message}",
                ErrorCategory.Prompt
            );
        }
    }
}