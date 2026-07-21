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
    private bool _loadFailed;

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
        var next = new List<PromptAction>(_cache) { action };
        SaveToDisk(next);
        _cache = next;
        ActionsChanged?.Invoke();
    }

    public void UpdateAction(PromptAction action)
    {
        EnsureCacheLoaded();
        var updated = action with { UpdatedAt = DateTime.UtcNow };
        var next = new List<PromptAction>(_cache);
        var idx = next.FindIndex(a => a.Id == action.Id);
        if (idx >= 0)
        {
            next[idx] = updated;
        }

        SaveToDisk(next);
        _cache = next;
        ActionsChanged?.Invoke();
    }

    public void DeleteAction(string id)
    {
        EnsureCacheLoaded();
        var next = new List<PromptAction>(_cache);
        next.RemoveAll(a => a.Id == id);
        SaveToDisk(next);
        _cache = next;
        ActionsChanged?.Invoke();
    }

    public void Reorder(IReadOnlyList<string> orderedIds)
    {
        EnsureCacheLoaded();
        var next = new List<PromptAction>(_cache);
        for (var i = 0; i < orderedIds.Count; i++)
        {
            var idx = next.FindIndex(a => a.Id == orderedIds[i]);
            if (idx >= 0)
            {
                next[idx] = next[idx] with { SortOrder = i };
            }
        }

        SaveToDisk(next);
        _cache = next;
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

        var next = new List<PromptAction>(_cache) { FirstRunDefaults.CreateAutoCleanupAction() };
        SaveToDisk(next);
        _cache = next;
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
            ),
        };

        var next = new List<PromptAction>(_cache);
        for (var i = 0; i < presets.Length; i++)
        {
            var (name, icon, prompt) = presets[i];
            next.Add(
                new PromptAction
                {
                    Id = Guid.NewGuid().ToString(),
                    Name = name,
                    SystemPrompt = prompt,
                    Icon = icon,
                    IsPreset = true,
                    SortOrder = i,
                }
            );
        }

        SaveToDisk(next);
        _cache = next;
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
                // A blank file is a benign "no actions yet" state (e.g. freshly created), not
                // corruption — leave the cache empty so normal saves still happen. Only non-empty
                // content that fails to parse is treated as a load failure below.
                if (!string.IsNullOrWhiteSpace(json))
                {
                    _cache = JsonSerializer.Deserialize<List<PromptAction>>(json) ?? [];
                }
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
            // The file exists but couldn't be parsed. Treat the cache as untrustworthy so a
            // later add/update doesn't overwrite the (possibly recoverable) file with an empty set.
            _loadFailed = true;
        }

        _cacheLoaded = true;
    }

    private void SaveToDisk(IReadOnlyList<PromptAction> actions)
    {
        if (_loadFailed)
        {
            // The cache is a partial set (the file didn't load), so refuse until it loads cleanly
            // rather than clobber the user's saved actions.
            const string reason =
                "the existing file could not be loaded, so writing now would overwrite saved actions";
            _errorLog?.AddEntry(
                $"Not saving prompt actions at {_filePath}: {reason}.",
                ErrorCategory.Prompt
            );
            throw new InvalidOperationException(
                $"Cannot save prompt actions at '{_filePath}': {reason}."
            );
        }

        try
        {
            var dir = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            var json = JsonSerializer.Serialize(actions, s_jsonOptions);
            AtomicFileWrite.WriteAllText(_filePath, json);
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[PromptActionService] Failed to save prompt actions to {_filePath}: {ex}");
            _errorLog?.AddEntry(
                $"Could not save prompt actions to {_filePath}: {ex.Message}",
                ErrorCategory.Prompt
            );
            throw;
        }
    }
}
