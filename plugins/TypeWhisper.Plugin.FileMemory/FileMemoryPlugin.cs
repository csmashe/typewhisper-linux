using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.Plugin.FileMemory;

public sealed partial class FileMemoryPlugin
    : IMemoryStoragePlugin,
        IActionPlugin,
        IPluginCollectionSettingsProvider,
        IPluginLocalizationAware
{
    private const int MaxContentLength = 32768;
    private const string CollectionKey = "memories";
    private const string ContentField = "content";
    private const string IdField = "__id";

    private static readonly JsonSerializerOptions s_jsonOptions = new() { WriteIndented = true };

    private static readonly HashSet<string> s_stopWords = new(
        "the and for with this that from have about please eine einen einer einem eines der die das den dem des und mit für von ist sind bitte über zum zur ich wir mir mich you your our what which how does nicht auch als auf aus bei ein an im es in is to of a"
            .Split(' '),
        StringComparer.OrdinalIgnoreCase
    );

    private readonly Lock _shownGate = new();
    private IPluginHostServices? _host;
    private IPluginLocalization? _injectedLocalization;
    private IPluginStateStore<ImmutableArray<MemoryEntry>>? _store;

    // Entries the editor last loaded. A save deletes only these, so facts stored meanwhile survive.
    private ImmutableHashSet<Guid> _shownIds = [];

    public string PluginId => "com.typewhisper.file-memory";
    public string PluginName => "File Memory";
    public string PluginVersion => PluginBuildInfo.Version;

    public string ActionId => "remember-in-file-memory";
    public string ActionName => Loc.L("Action.Name");
    public string ActionIcon => "\U0001F4BE";

    private IPluginLocalization? Loc => _host?.Localization ?? _injectedLocalization;

    public void SetLocalization(IPluginLocalization localization) =>
        _injectedLocalization = localization;

    public Task ActivateAsync(IPluginHostServices host)
    {
        _host = host;
        _store = host.OpenStateStore<ImmutableArray<MemoryEntry>>(
            "memories.json",
            static () => [],
            new PluginStateStoreOptions
            {
                JsonOptions = s_jsonOptions,
                CorruptFilePolicy = PluginStateCorruptFilePolicy.PreserveAndReset,
            }
        );
        _host.Log(PluginLogLevel.Info, "Activated");
        return Task.CompletedTask;
    }

    public Task DeactivateAsync()
    {
        // _shownIds is kept so a draft open across a disable/enable cycle keeps its deletions.
        _host = null;
        _store = null;
        return Task.CompletedTask;
    }

    public async Task StoreAsync(string content, CancellationToken ct = default)
    {
        await StoreCoreAsync(ValidateContent(content), ct);
    }

    // True when the entry was added, false when identical content was already stored.
    private async Task<bool> StoreCoreAsync(string content, CancellationToken ct)
    {
        var added = false;
        var committed = await GetStore().UpdateAsync(
            current =>
            {
                if (current.Any(e => e.Content == content))
                {
                    return current;
                }

                added = true;
                return WithIds(current).Add(NewEntry(content, Guid.NewGuid()));
            },
            ct
        );
        if (!added)
        {
            _host?.Log(PluginLogLevel.Debug, "Duplicate memory skipped");
            return false;
        }

        _host?.Log(PluginLogLevel.Debug, $"Stored memory (total={committed.Length})");
        return true;
    }

    /// <summary>
    ///     Ranks entries by shared words (stop words and one-letter words ignored); an entry
    ///     containing the whole query as a phrase ranks first. Ties go to the newest entry.
    /// </summary>
    public async Task<IReadOnlyList<string>> SearchAsync(
        string query,
        int maxResults = 5,
        CancellationToken ct = default
    )
    {
        ArgumentNullException.ThrowIfNull(query);
        var entries = await GetStore().ReadAsync(ct);
        return Rank(entries, query, maxResults);
    }

    private static List<string> Rank(
        ImmutableArray<MemoryEntry> entries,
        string query,
        int maxResults
    )
    {
        var trimmed = query.Trim();
        var terms = MemoryTerms(trimmed);
        var matchPhrase = terms.Count > 0 && trimmed.Length >= 3;
        return entries
            .Select(entry => (Entry: entry, Score: Score(entry.Content)))
            .Where(match => match.Score > 0)
            .OrderByDescending(match => match.Score)
            .ThenByDescending(match => match.Entry.CreatedAt)
            .Take(Math.Clamp(maxResults, 0, 1000))
            .Select(match => match.Entry.Content)
            .ToList();

        int Score(string content)
        {
            if (trimmed.Length == 0)
            {
                return 1;
            }

            return matchPhrase && content.Contains(trimmed, StringComparison.OrdinalIgnoreCase)
                ? 1000
                : MemoryTerms(content).Count(terms.Contains);
        }
    }

    private static HashSet<string> MemoryTerms(string text) =>
        WordPattern()
            .Matches(text)
            .Select(match => match.Value)
            .Where(term => term.Length >= 2 && !s_stopWords.Contains(term))
            .Take(256)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    public async Task<IReadOnlyList<string>> GetAllAsync(CancellationToken ct = default)
    {
        var entries = await GetStore().ReadAsync(ct);
        return entries.Select(e => e.Content).ToList();
    }

    public async Task DeleteAsync(string content, CancellationToken ct = default)
    {
        await GetStore().UpdateAsync(
            current =>
            {
                var next = current.Where(e => e.Content != content).ToImmutableArray();
                return next.Length == current.Length ? current : next;
            },
            ct
        );
    }

    public async Task ClearAllAsync(CancellationToken ct = default)
    {
        var cleared = false;
        await GetStore().UpdateAsync(
            current =>
            {
                cleared = !current.IsEmpty;
                return cleared ? [] : current;
            },
            ct
        );
        if (cleared)
        {
            _host?.Log(PluginLogLevel.Info, "All memories cleared");
        }
    }

    public async Task<int> CountAsync(CancellationToken ct = default)
    {
        return (await GetStore().ReadAsync(ct)).Length;
    }

    public async Task<ActionResult> ExecuteAsync(
        string input,
        ActionContext context,
        CancellationToken ct
    )
    {
        var content = input.Trim();
        // ReSharper disable once ConvertIfStatementToSwitchStatement -- two guard clauses, each with its own message, ahead of the store; a switch on the length would hide that.
        if (content.Length == 0)
        {
            return new ActionResult(false, Loc.L("Action.Empty"));
        }

        if (content.Length > MaxContentLength)
        {
            return new ActionResult(false, Loc.L("Memory.TooLong", MaxContentLength));
        }

        return await StoreCoreAsync(content, ct)
            ? new ActionResult(true, Loc.L("Action.Stored"))
            : new ActionResult(true, Loc.L("Action.AlreadyStored"));
    }

    public IReadOnlyList<PluginCollectionDefinition> GetCollectionDefinitions() =>
    [
        new(
            CollectionKey,
            Loc.L("Collection.Label"),
            _store is null
                ? Loc.L("Collection.DisabledDescription")
                : Loc.L("Collection.Description"),
            [
                new PluginSettingDefinition(
                    ContentField,
                    Loc.L("Collection.Content"),
                    Placeholder: Loc.L("Collection.ContentPlaceholder"),
                    Kind: PluginSettingKind.Multiline
                ),
                new PluginSettingDefinition(IdField, IdField),
            ],
            ContentField,
            Loc.L("Collection.Add")
        )
        {
            IsSearchable = true,
        },
    ];

    public async Task<IReadOnlyList<PluginCollectionItem>> GetItemsAsync(
        string collectionKey,
        CancellationToken ct = default
    )
    {
        if (collectionKey != CollectionKey)
        {
            return [];
        }

        if (_store is not { } store)
        {
            // The editor now shows nothing, so nothing it later saves may count as deleted.
            lock (_shownGate)
            {
                _shownIds = [];
            }

            return [];
        }

        var entries = WithIds(await store.ReadAsync(ct));
        lock (_shownGate)
        {
            _shownIds = entries.Select(entry => entry.Id).ToImmutableHashSet();
        }

        return entries
            .Select(entry => new PluginCollectionItem(
                new Dictionary<string, string?>
                {
                    [ContentField] = entry.Content,
                    [IdField] = entry.Id.ToString("D"),
                }
            ))
            .ToList();
    }

    public async Task<PluginSettingsValidationResult> SetItemsAsync(
        string collectionKey,
        IReadOnlyList<PluginCollectionItem> items,
        CancellationToken ct = default
    )
    {
        if (collectionKey != CollectionKey)
        {
            return new PluginSettingsValidationResult(false, Loc.L("Collection.Unknown"));
        }

        if (_store is not { } store)
        {
            return new PluginSettingsValidationResult(
                false,
                Loc.L("Collection.DisabledDescription")
            );
        }

        var submitted = new List<(Guid Id, string Content)>(items.Count);
        var contents = new HashSet<string>(StringComparer.Ordinal);
        var ids = new HashSet<Guid>();
        foreach (var item in items)
        {
            var content = (item.Values.GetValueOrDefault(ContentField) ?? "").Trim();
            // ReSharper disable once ConvertIfStatementToSwitchStatement -- guard chain continues with the duplicate check, which is not about the length.
            if (content.Length == 0)
            {
                return new PluginSettingsValidationResult(false, Loc.L("Memory.Empty"));
            }

            if (content.Length > MaxContentLength)
            {
                return new PluginSettingsValidationResult(
                    false,
                    Loc.L("Memory.TooLong", MaxContentLength)
                );
            }

            if (!contents.Add(content))
            {
                return new PluginSettingsValidationResult(false, Loc.L("Memory.Duplicate"));
            }

            var id = Guid.TryParse(item.Values.GetValueOrDefault(IdField), out var parsed)
                     && parsed != Guid.Empty
                     && !ids.Contains(parsed)
                ? parsed
                : Guid.NewGuid();
            ids.Add(id);
            submitted.Add((id, content));
        }

        ImmutableHashSet<Guid> shown;
        lock (_shownGate)
        {
            shown = _shownIds;
        }

        var committed = await store.UpdateAsync(
            current => Merge(WithIds(current), submitted, shown),
            ct
        );
        lock (_shownGate)
        {
            _shownIds = WithIds(committed).Select(entry => entry.Id).ToImmutableHashSet();
        }

        return new PluginSettingsValidationResult(true, string.Empty);
    }

    // The editor's list in its order, then entries the editor never showed (added meanwhile)
    // unless an edit now holds the same text. Shown entries missing from the list are deleted.
    private static ImmutableArray<MemoryEntry> Merge(
        ImmutableArray<MemoryEntry> current,
        IReadOnlyList<(Guid Id, string Content)> submitted,
        ImmutableHashSet<Guid> shown
    )
    {
        var byId = current.ToDictionary(entry => entry.Id);
        var builder = ImmutableArray.CreateBuilder<MemoryEntry>();
        var contents = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (id, content) in submitted)
        {
            builder.Add(
                byId.TryGetValue(id, out var existing)
                    ? existing with { Content = content }
                    : NewEntry(content, id)
            );
            contents.Add(content);
        }

        var submittedIds = submitted.Select(item => item.Id).ToHashSet();
        // ReSharper disable once ForeachCanBePartlyConvertedToQueryUsingAnotherGetEnumerator -- contents.Add is a side effect of the filter, so a LINQ Where would hide a mutation.
        foreach (var entry in current)
        {
            if (
                !submittedIds.Contains(entry.Id)
                && !shown.Contains(entry.Id)
                && contents.Add(entry.Content)
            )
            {
                builder.Add(entry);
            }
        }

        var merged = builder.ToImmutable();
        return merged.SequenceEqual(current) ? current : merged;
    }

    private static string ValidateContent(string content)
    {
        var trimmed = content.Trim();
        if (trimmed.Length is 0 or > MaxContentLength)
        {
            throw new ArgumentException(
                $"Memory content must be between 1 and {MaxContentLength} characters.",
                nameof(content)
            );
        }

        return trimmed;
    }

    private static MemoryEntry NewEntry(string content, Guid id) =>
        new(content, DateTime.UtcNow) { Id = id };

    // Entries saved before ids existed get one derived from their (unique) content, so the
    // editor sees the same id on every load without a migration write.
    private static ImmutableArray<MemoryEntry> WithIds(ImmutableArray<MemoryEntry> entries) =>
        entries.Any(entry => entry.Id == Guid.Empty)
            ? entries
                .Select(entry => entry.Id == Guid.Empty ? entry with { Id = LegacyId(entry) } : entry)
                .ToImmutableArray()
            : entries;

    private static Guid LegacyId(MemoryEntry entry) =>
        new(SHA256.HashData(Encoding.UTF8.GetBytes(entry.Content)).AsSpan(0, 16));

    private IPluginStateStore<ImmutableArray<MemoryEntry>> GetStore() =>
        _store ?? throw new InvalidOperationException("Plugin not activated");

    public void Dispose() { }

    [GeneratedRegex(@"[\p{L}\p{N}]+")]
    private static partial Regex WordPattern();

    private sealed record MemoryEntry(string Content, DateTime CreatedAt)
    {
        public Guid Id { get; init; }
    }
}
