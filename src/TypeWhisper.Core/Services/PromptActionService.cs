using System.Collections.Immutable;
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

    private static readonly (string NameKey, string Icon, string Prompt)[] s_presets =
    [
        (
            DefaultItemNames.TranslateToEnglish,
            "\U0001F30D",
            "Translate the following text to English. Return only the translated text, no explanations."
        ),
        (
            DefaultItemNames.WriteEmail,
            "\u2709\uFE0F",
            "Rewrite the following text as a professional email. Keep the same meaning and tone but make it polished and suitable for business communication. Return only the email body."
        ),
        (
            DefaultItemNames.FormatAsList,
            "\U0001F4CB",
            "Convert the following text into a clean bullet-point list. Return only the formatted list."
        ),
        (
            DefaultItemNames.ActionItems,
            "\u2705",
            "Extract all action items and tasks from the following text. Return them as a numbered list. If no action items are found, say so briefly."
        ),
        (
            DefaultItemNames.Reply,
            "\U0001F4AC",
            "Write a concise, professional reply to the following message. Match the tone of the original. Return only the reply text."
        ),
    ];

    private readonly string _filePath;
    private readonly IErrorLogService? _errorLog;
    private readonly IDefaultNameLocalizer _names;
    private readonly AtomicJsonStore<ImmutableArray<PromptAction>> _store;

    public PromptActionService(
        string filePath,
        IErrorLogService? errorLog = null,
        IDefaultNameLocalizer? names = null
    )
    {
        _filePath = Path.GetFullPath(filePath);
        _errorLog = errorLog;
        _names = names ?? DefaultItemNames.English;
        _store = new AtomicJsonStore<ImmutableArray<PromptAction>>(
            _filePath,
            static () => [],
            new AtomicJsonStoreOptions<ImmutableArray<PromptAction>>
            {
                JsonOptions = s_jsonOptions,
                CorruptFilePolicy = AtomicJsonCorruptFilePolicy.PreserveAndReset,
                Deserialize = json =>
                {
                    var actions = JsonSerializer.Deserialize<ImmutableArray<PromptAction>>(
                        json,
                        s_jsonOptions
                    );
                    return actions.IsDefault
                        ? throw new JsonException("Prompt-action JSON deserialized to null.")
                        : actions;
                },
                Diagnostic = ReportDiagnostic,
            }
        );
    }

    public IReadOnlyList<PromptAction> Actions => _store.Current.ToArray();

    public bool TryGetActions(out IReadOnlyList<PromptAction> actions)
    {
        actions = _store.GetCurrent(out var reset).ToArray();
        return !reset;
    }

    public IReadOnlyList<PromptAction> EnabledActions
    {
        get
        {
            return _store.Current.Where(a => a.IsEnabled).OrderBy(a => a.SortOrder).ToList();
        }
    }

    public event Action? ActionsChanged;

    public void AddAction(PromptAction action)
    {
        Commit(current => current.Add(action));
    }

    public void UpdateAction(PromptAction action)
    {
        Commit(
            current =>
            {
                var idx = FindIndex(current, a => a.Id == action.Id);
                if (idx < 0)
                {
                    return current;
                }

                var existing = current[idx];
                return current.SetItem(idx, action with
                {
                    UpdatedAt = DateTime.UtcNow,
                    DefaultNameKey = existing.DefaultNameKey ?? ResolveDefaultNameKey(existing),
                    IsNameCustomized = existing.IsNameCustomized || action.IsNameCustomized
                        || !string.Equals(existing.Name, action.Name, StringComparison.Ordinal),
                });
            }
        );
    }

    public void DeleteAction(string id)
    {
        Commit(
            current =>
            {
                var next = current.Where(a => a.Id != id).ToImmutableArray();
                return next.Length == current.Length ? current : next;
            }
        );
    }

    public void Reorder(IReadOnlyList<string> orderedIds)
    {
        Commit(
            current =>
            {
                var next = current;
                var changed = false;
                for (var i = 0; i < orderedIds.Count; i++)
                {
                    var orderedId = orderedIds[i];
                    var idx = FindIndex(next, a => a.Id == orderedId);
                    if (idx < 0 || next[idx].SortOrder == i)
                    {
                        continue;
                    }

                    next = next.SetItem(idx, next[idx] with { SortOrder = i });
                    changed = true;
                }

                return changed ? next : current;
            }
        );
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

        Commit(
            current =>
                current.Any(a => a.Id == FirstRunDefaults.AutoCleanupActionId)
                    ? current
                    : current.Add(FirstRunDefaults.CreateAutoCleanupAction(_names))
        );
    }

    public void SeedPresets()
    {
        Commit(
            current =>
            {
                if (current.Any(a => a.IsPreset))
                {
                    return current;
                }

                var next = current;
                for (var i = 0; i < s_presets.Length; i++)
                {
                    var (nameKey, icon, prompt) = s_presets[i];
                    next = next.Add(
                        new PromptAction
                        {
                            Id = Guid.NewGuid().ToString(),
                            Name = _names.GetName(nameKey),
                            DefaultNameKey = nameKey,
                            SystemPrompt = prompt,
                            Icon = icon,
                            IsPreset = true,
                            SortOrder = i,
                        }
                    );
                }

                return next;
            }
        );
    }

    public void LocalizeDefaultNames()
    {
        // A no-op commit would still write a missing or unreadable file, which would stop
        // first-run seeding or overwrite what the user could recover, so commit only a real rename.
        if (!File.Exists(_filePath))
        {
            return;
        }

        var current = _store.GetCurrent(out var reset);
        if (reset || LocalizeDefaultNames(current).Equals(current))
        {
            return;
        }

        Commit(LocalizeDefaultNames);
    }

    private ImmutableArray<PromptAction> LocalizeDefaultNames(ImmutableArray<PromptAction> current)
    {
        var next = current;
        for (var i = 0; i < next.Length; i++)
        {
            var action = next[i];
            var key = ResolveDefaultNameKey(action);
            if (action.IsNameCustomized || key is null || !_names.IsShippedName(key, action.Name))
            {
                continue;
            }

            var name = _names.GetName(key);
            if (name != action.Name || key != action.DefaultNameKey)
            {
                next = next.SetItem(i, action with { Name = name, DefaultNameKey = key });
            }
        }

        return next;
    }

    private string? ResolveDefaultNameKey(PromptAction action)
    {
        if (action.Id == FirstRunDefaults.AutoCleanupActionId)
        {
            return DefaultItemNames.AutoCleanupAction;
        }

        if (!action.IsPreset)
        {
            return null;
        }

        if (action.DefaultNameKey is not null)
        {
            return DefaultItemNames.PresetKeys.Contains(action.DefaultNameKey)
                ? action.DefaultNameKey
                : null;
        }

        // Legacy presets have random IDs and no name metadata. Requiring both the original
        // prompt and its own shipped name avoids treating an email prompt renamed "Reply"
        // as the reply preset. Ambiguous or edited legacy entries keep their stored names.
        foreach (var preset in s_presets)
        {
            if (action.SystemPrompt == preset.Prompt && _names.IsShippedName(preset.NameKey, action.Name))
            {
                return preset.NameKey;
            }
        }

        return null;
    }

    private void Commit(
        Func<ImmutableArray<PromptAction>, ImmutableArray<PromptAction>> update
    )
    {
        var changed = false;
        try
        {
            _store.Update(
                current =>
                {
                    var next = update(current);
                    changed = !next.Equals(current);
                    return next;
                }
            );
        }
        catch (Exception ex)
        {
            Trace.WriteLine(
                $"[PromptActionService] Failed to save prompt actions to {_filePath}: {ex}"
            );
            _errorLog?.AddEntry(
                $"Could not save prompt actions to {_filePath}: {ex.Message}",
                ErrorCategory.Prompt
            );
            throw;
        }

        if (changed)
        {
            ActionsChanged?.Invoke();
        }
    }

    private void ReportDiagnostic(AtomicJsonStoreDiagnostic diagnostic)
    {
        if (diagnostic.Kind != AtomicJsonStoreDiagnosticKind.PrimaryCorrupt)
        {
            return;
        }

        _errorLog?.AddEntry(
            $"Could not load saved prompt actions from {_filePath}: "
            + (diagnostic.Exception?.Message ?? "invalid JSON"),
            ErrorCategory.Prompt
        );
    }

    private static int FindIndex(
        ImmutableArray<PromptAction> actions,
        Func<PromptAction, bool> predicate
    )
    {
        for (var index = 0; index < actions.Length; index++)
        {
            if (predicate(actions[index]))
            {
                return index;
            }
        }

        return -1;
    }
}
