using System.Collections.Immutable;
using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;

namespace TypeWhisper.Core.Services;

/// <summary>
///     File-backed <see cref="IDictionaryService" />: persists terms and corrections as JSON,
///     lazily loaded and cached, with atomic writes that refuse to overwrite a file that failed
///     to load so a transient IO error can't discard the user's dictionary.
/// </summary>
public sealed partial class DictionaryService : IDictionaryService
{
    private static readonly JsonSerializerOptions s_jsonOptions = new() { WriteIndented = true };

    private readonly AtomicJsonStore<ImmutableArray<DictionaryEntry>> _store;

    public DictionaryService(string filePath)
    {
        _store = new AtomicJsonStore<ImmutableArray<DictionaryEntry>>(
            filePath,
            static () => [],
            new AtomicJsonStoreOptions<ImmutableArray<DictionaryEntry>>
            {
                JsonOptions = s_jsonOptions,
                CorruptFilePolicy = AtomicJsonCorruptFilePolicy.PreserveAndReset,
                Deserialize = json =>
                {
                    var entries = JsonSerializer.Deserialize<ImmutableArray<DictionaryEntry>>(
                        json,
                        s_jsonOptions
                    );
                    return entries.IsDefault
                        ? throw new JsonException("Dictionary JSON deserialized to null.")
                        : entries;
                },
            }
        );
    }

    public IReadOnlyList<DictionaryEntry> Entries => _store.Current.ToArray();

    public event Action? EntriesChanged;

    public void AddEntry(DictionaryEntry entry)
    {
        Commit(entries =>
        {
            entries.Add(entry);
            return true;
        });
    }

    public void AddEntries(IEnumerable<DictionaryEntry> entries)
    {
        var additions = entries.ToList();
        if (additions.Count == 0)
        {
            return;
        }

        Commit(current =>
        {
            current.AddRange(additions);
            return true;
        });
    }

    public void UpdateEntry(DictionaryEntry entry)
    {
        Commit(entries =>
        {
            var idx = entries.FindIndex(e => e.Id == entry.Id);
            if (idx < 0)
            {
                return false;
            }

            entries[idx] = entry;
            return true;
        });
    }

    public void DeleteEntry(string id)
    {
        Commit(entries =>
        {
            return entries.RemoveAll(e => e.Id == id) > 0;
        });
    }

    public void DeleteEntries(IEnumerable<string> ids)
    {
        var idSet = ids.ToHashSet();
        Commit(entries =>
        {
            return entries.RemoveAll(e => idSet.Contains(e.Id)) > 0;
        });
    }

    public string ApplyCorrections(string text)
    {
        return ApplyCorrectionsCore(text, recordUsage: true);
    }

    /// <summary>
    ///     Side-effect-free variant of <see cref="ApplyCorrections" />: never records usage
    ///     counts or writes the dictionary file (audit §2 M3).
    /// </summary>
    public string PreviewCorrections(string text)
    {
        return ApplyCorrectionsCore(text, recordUsage: false);
    }

    private string ApplyCorrectionsCore(string text, bool recordUsage)
    {
        var corrections = _store.Current
                .Where(e =>
                    e is { IsEnabled: true, EntryType: DictionaryEntryType.Correction }
                    && !string.IsNullOrEmpty(e.Original)
                    && e.Replacement is not null
                )
                .OrderByDescending(e => e.Priority)
                .ThenByDescending(e => e.IsStarred)
                .ThenByDescending(e => e.Original.Length)
                .ToList();

        // Accumulate usage increments and persist once at the end to avoid N file writes per dictation.
        var usedCounts = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var entry in corrections)
        {
            var comparison = entry.CaseSensitive
                ? StringComparison.Ordinal
                : StringComparison.OrdinalIgnoreCase;

            if (!text.Contains(entry.Original, comparison))
            {
                continue;
            }

            var pattern = Regex.Escape(entry.Original);
            var options = entry.CaseSensitive ? RegexOptions.None : RegexOptions.IgnoreCase;
            // \b silently fails for originals like "C#" or ".NET" whose ends are non-word chars.
            // Anchor each side based on what the original starts/ends with: \b on word-chars,
            // lookaround on symbol-chars.
            var prefix = char.IsLetterOrDigit(entry.Original[0]) || entry.Original[0] == '_'
                ? @"\b"
                : @"(?<=\W|^)";
            var lastChar = entry.Original[^1];
            var suffix = char.IsLetterOrDigit(lastChar) || lastChar == '_'
                ? @"\b"
                : @"(?=\W|$)";
            // MatchEvaluator overload: prevents "$1"/"$&" in user replacements from being
            // interpreted as regex substitution tokens; also counts each match individually.
            var replacement = entry.Replacement!;
            var matchCount = 0;
            var replaced = Regex.Replace(
                text,
                prefix + pattern + suffix,
                _ =>
                {
                    matchCount++;
                    return replacement;
                },
                options
            );
            if (matchCount == 0 || string.Equals(replaced, text, StringComparison.Ordinal))
            {
                continue;
            }

            text = replaced;
            usedCounts[entry.Id] = usedCounts.TryGetValue(entry.Id, out var prior)
                ? prior + matchCount
                : matchCount;
        }

        if (recordUsage && usedCounts.Count > 0)
        {
            IncrementUsageCounts(usedCounts);
        }

        return text;
    }

    public string? GetTermsForPrompt()
    {
        var terms = GetEnabledTerms();

        return terms.Count == 0 ? null : string.Join(", ", terms);
    }

    public IReadOnlyList<string> GetEnabledTerms()
    {
        return NormalizeTerms(
            _store.Current
                .Where(e => e is { IsEnabled: true, EntryType: DictionaryEntryType.Term })
                .Select(e => e.Original)
        );
    }

    public void SetTerms(IEnumerable<string> terms, bool replaceExisting)
    {
        var normalized = NormalizeTerms(terms);
        Commit(newCache =>
        {
            var desiredByKey = normalized.ToDictionary(TermKey, term => term);
            var existingTerms = newCache.Where(e => e.EntryType == DictionaryEntryType.Term).ToList();
            var changed = false;

            foreach (var entry in existingTerms)
            {
                var key = TermKey(entry.Original);
                if (desiredByKey.ContainsKey(key))
                {
                    var idx = newCache.FindIndex(e => e.Id == entry.Id);
                    if (idx < 0 || entry.IsEnabled)
                    {
                        continue;
                    }

                    newCache[idx] = entry with { IsEnabled = true };
                    changed = true;
                }
                else if (replaceExisting)
                {
                    newCache.Remove(entry);
                    changed = true;
                }
            }

            var existingKeys = existingTerms
                .Select(e => TermKey(e.Original))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            // ReSharper disable once LoopCanBeConvertedToQuery
            foreach (var term in normalized.Where(term => !existingKeys.Contains(TermKey(term))))
            {
                newCache.Add(
                    new DictionaryEntry
                    {
                        Id = Guid.NewGuid().ToString(), EntryType = DictionaryEntryType.Term, Original = term,
                    }
                );
                changed = true;
            }

            return changed;
        });
    }

    public void RemoveAllTerms()
    {
        Commit(entries =>
        {
            return entries.RemoveAll(e => e.EntryType == DictionaryEntryType.Term) > 0;
        });
    }

    public bool DeleteTerm(string term)
    {
        if (string.IsNullOrWhiteSpace(term))
        {
            return false;
        }

        var removed = Commit(entries =>
        {
            return entries.RemoveAll(e =>
                    e.EntryType == DictionaryEntryType.Term
                    && e.Original.Trim().Equals(term.Trim(), StringComparison.OrdinalIgnoreCase)
                )
                > 0;
        });

        return removed;
    }

    public IReadOnlyList<DictionaryCorrection> GetCorrections()
    {
        return _store.Current
            .Where(e =>
                e is { IsEnabled: true, EntryType: DictionaryEntryType.Correction, Replacement: not null }
            )
            .Select(e => new DictionaryCorrection(e.Original, e.Replacement!, e.CaseSensitive))
            .ToList();
    }

    public DictionaryCorrection UpsertCorrection(
        string original,
        string replacement,
        bool caseSensitive
    )
    {
        if (string.IsNullOrWhiteSpace(original))
        {
            throw new ArgumentException("Original must not be empty.", nameof(original));
        }

        ArgumentNullException.ThrowIfNull(replacement);

        Commit(newCache =>
        {
            var existing = newCache.FirstOrDefault(e =>
                e.EntryType == DictionaryEntryType.Correction
                && e.Original.Equals(original, StringComparison.OrdinalIgnoreCase)
            );

            if (existing is not null)
            {
                var idx = newCache.FindIndex(e => e.Id == existing.Id);
                if (idx >= 0)
                {
                    newCache[idx] = existing with
                    {
                        Replacement = replacement, CaseSensitive = caseSensitive, IsEnabled = true,
                    };
                }
            }
            else
            {
                newCache.Add(
                    new DictionaryEntry
                    {
                        Id = Guid.NewGuid().ToString(),
                        EntryType = DictionaryEntryType.Correction,
                        Original = original,
                        Replacement = replacement,
                        CaseSensitive = caseSensitive,
                        Source = DictionaryEntrySource.Manual,
                    }
                );
            }

            return true;
        });
        return new DictionaryCorrection(original, replacement, caseSensitive);
    }

    public bool DeleteCorrection(string original)
    {
        if (string.IsNullOrWhiteSpace(original))
        {
            return false;
        }

        var removed = Commit(entries =>
        {
            return entries.RemoveAll(e =>
                    e.EntryType == DictionaryEntryType.Correction
                    && e.Original.Equals(original, StringComparison.OrdinalIgnoreCase)
                )
                > 0;
        });

        return removed;
    }

    public void LearnCorrection(string original, string replacement)
    {
        Commit(newCache =>
        {
            var existing = newCache.FirstOrDefault(e =>
                e.EntryType == DictionaryEntryType.Correction
                && e.Original.Equals(original, StringComparison.OrdinalIgnoreCase)
            );

            if (existing is not null)
            {
                // A user-authored or imported mapping always wins over silent auto-learning:
                // overwriting it would turn one observed edit into a replacement the user
                // explicitly configured differently, with no notice.
                if (existing.Source is DictionaryEntrySource.Manual or DictionaryEntrySource.Import)
                {
                    return false;
                }

                var idx = newCache.FindIndex(e => e.Id == existing.Id);
                if (idx >= 0)
                {
                    newCache[idx] = existing with
                    {
                        Replacement = replacement,
                        UsageCount = existing.UsageCount + 1,
                        TimesCorrected = existing.TimesCorrected + 1,
                        LastCorrectedAt = DateTime.UtcNow,
                    };
                }
            }
            else
            {
                newCache.Add(
                    new DictionaryEntry
                    {
                        Id = Guid.NewGuid().ToString(),
                        EntryType = DictionaryEntryType.Correction,
                        Original = original,
                        Replacement = replacement,
                        TimesCorrected = 1,
                        LastCorrectedAt = DateTime.UtcNow,
                        Source = DictionaryEntrySource.CorrectionSuggestion,
                    }
                );
            }

            return true;
        });
    }

    public IReadOnlyList<LearnedDictionaryCorrection> LearnCorrections(
        IEnumerable<CorrectionSuggestion> suggestions,
        IReadOnlySet<string>? replaceableEntryIds = null
    )
    {
        var suggestionList = suggestions.ToList();
        var learned = new List<LearnedDictionaryCorrection>();
        Commit(newCache =>
        {
            learned = [];
            var changed = false;

            // First occurrence of an original within one batch wins; later ones are dropped.
            var seenOriginals = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var suggestion in suggestionList)
            {
                var original = suggestion.Original.Trim();
                var replacement = suggestion.Replacement.Trim();

                if (original.Length == 0
                    || replacement.Length == 0
                    || string.Equals(original, replacement, StringComparison.OrdinalIgnoreCase)
                    || !IsSafeAutomaticallyLearnedToken(original)
                    || !IsSafeAutomaticallyLearnedToken(replacement)
                    || !seenOriginals.Add(original))
                {
                    continue;
                }

                var existing = newCache.FirstOrDefault(e =>
                    e.EntryType == DictionaryEntryType.Correction
                    && e.Original.Equals(original, StringComparison.OrdinalIgnoreCase)
                );

                if (existing is not null)
                {
                    // The only entries we may overwrite are the session-created ones the caller is
                    // self-healing (idle then final commit of the same utterance). Every other
                    // existing correction is left as-is, regardless of source.
                    if (replaceableEntryIds is null || !replaceableEntryIds.Contains(existing.Id))
                    {
                        continue;
                    }

                    var idx = newCache.FindIndex(e => e.Id == existing.Id);
                    if (idx < 0)
                    {
                        continue;
                    }

                    var updated = existing with
                    {
                        Replacement = replacement,
                        TimesCorrected = existing.TimesCorrected + 1,
                        LastCorrectedAt = DateTime.UtcNow,
                    };
                    newCache[idx] = updated;
                    learned.Add(
                        new LearnedDictionaryCorrection(updated.Id, updated.Original, replacement)
                    );
                    changed = true;
                    continue;
                }

                var entry = new DictionaryEntry
                {
                    Id = Guid.NewGuid().ToString(),
                    EntryType = DictionaryEntryType.Correction,
                    Original = original,
                    Replacement = replacement,
                    TimesCorrected = 1,
                    LastCorrectedAt = DateTime.UtcNow,
                    Source = DictionaryEntrySource.AutoLearned,
                };
                newCache.Add(entry);
                learned.Add(new LearnedDictionaryCorrection(entry.Id, entry.Original, replacement));
                changed = true;
            }

            return changed;
        });

        return learned;
    }

    public void UndoLearnedCorrections(IEnumerable<LearnedDictionaryCorrection> learnedCorrections)
    {
        var learnedIds = learnedCorrections
            .Select(c => c.Id)
            .ToHashSet(StringComparer.Ordinal);
        if (learnedIds.Count == 0)
        {
            return;
        }

        Commit(entries =>
        {
            return entries.RemoveAll(e =>
                    e.EntryType == DictionaryEntryType.Correction && learnedIds.Contains(e.Id)
                )
                > 0;
        });
    }

    // Guards silent auto-learning against picking up punctuation-fenced or multi-word fragments:
    // the first and last chars must be alphanumeric and the interior only letters/digits/hyphen/apostrophe.
    private static bool IsSafeAutomaticallyLearnedToken(string token)
    {
        if (token.Length == 0
            || !char.IsLetterOrDigit(token[0])
            || !char.IsLetterOrDigit(token[^1]))
        {
            return false;
        }

        return token.All(static c =>
            char.IsLetterOrDigit(c) || c == '-' || c == '\'');
    }

    public void ActivatePack(TermPack pack)
    {
        Commit(entries =>
        {
            // Deterministic IDs "pack:<packId>:<term>" enable duplicate detection and clean removal in DeactivatePack.
            var existingPackIds = entries
                .Where(e => e.EntryType == DictionaryEntryType.Term)
                .Select(e => e.Id)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var newEntries = pack
                .Terms.Where(t => !existingPackIds.Contains($"pack:{pack.Id}:{t}"))
                .Select(t => new DictionaryEntry
                {
                    Id = $"pack:{pack.Id}:{t}", EntryType = DictionaryEntryType.Term, Original = t,
                })
                .ToList();

            if (newEntries.Count == 0)
            {
                return false;
            }

            entries.AddRange(newEntries);
            return true;
        });
    }

    public void DeactivatePack(string packId)
    {
        Commit(entries =>
        {
            var prefix = $"pack:{packId}:";
            return entries.RemoveAll(e => e.Id.StartsWith(prefix, StringComparison.Ordinal)) > 0;
        });
    }

    public void ApplyIndustryPreset(string presetId)
    {
        var preset = IndustryPreset.All.FirstOrDefault(p =>
            string.Equals(p.Id, presetId, StringComparison.OrdinalIgnoreCase)
        );
        if (preset?.TermPackId is not { } packId)
        {
            return;
        }

        var pack = TermPack.FindById(packId);
        if (pack is not null)
        {
            ActivatePack(pack);
        }
    }

    private void IncrementUsageCounts(Dictionary<string, int> deltas)
    {
        try
        {
            Commit(
                entries =>
            {
                    var now = DateTime.UtcNow;
                    var changed = false;
                    foreach (var (id, delta) in deltas)
                    {
                        if (delta <= 0)
                        {
                            continue;
                        }

                        var idx = entries.FindIndex(e => e.Id == id);
                        if (idx < 0)
                        {
                            continue;
                        }

                        entries[idx] = entries[idx] with
                        {
                            UsageCount = entries[idx].UsageCount + delta,
                            TimesApplied = entries[idx].TimesApplied + delta,
                            LastUsedAt = now,
                        };
                        changed = true;
                    }

                    return changed;
                },
                raiseEvent: false
            );
        }
        catch (Exception ex)
        {
            // Usage tracking is best-effort — the corrected text is already produced.
            // The store publishes only after commit, so failure retains prior counters.
            Trace.WriteLine(
                $"[DictionaryService] Could not persist usage counts for {deltas.Count} entries: {ex.Message}"
            );
        }
    }

    private bool Commit(
        Func<List<DictionaryEntry>, bool> update,
        bool raiseEvent = true
    )
    {
        var changed = false;
        _store.Update(
            current =>
            {
                var next = current.ToList();
                changed = update(next);
                return changed ? [.. next] : current;
            }
        );

        if (changed && raiseEvent)
        {
            EntriesChanged?.Invoke();
        }

        return changed;
    }

    private static List<string> NormalizeTerms(IEnumerable<string> terms)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var normalized = new List<string>();

        // ReSharper disable once LoopCanBeConvertedToQuery
        foreach (var rawTerm in terms)
        {
            var term = rawTerm.Trim();
            if (term.Length == 0)
            {
                continue;
            }

            if (seen.Add(TermKey(term)))
            {
                normalized.Add(term);
            }
        }

        return normalized;
    }

    private static string TermKey(string term)
    {
        return term.Trim().ToUpperInvariant();
    }
}
