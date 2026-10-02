using Moq;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;
using TypeWhisper.Core.Services;
using TypeWhisper.Linux.Services;
using TypeWhisper.Linux.Services.Localization;
using TypeWhisper.Linux.Services.Plugins;
using TypeWhisper.Linux.Services.Vocabulary;
using TypeWhisper.Linux.ViewModels.Sections;
using TypeWhisper.PluginSDK;
using TypeWhisper.Tests;
using Xunit;

namespace TypeWhisper.Linux.Tests;

public sealed class DictionarySectionViewModelTests : IDisposable
{
    private readonly string _tempDir;
    private readonly PluginManager _plugins = TestPluginManagerFactory.Create();
    private readonly List<ModelManagerService> _models = [];

    public DictionarySectionViewModelTests()
    {
        _tempDir = Path.Join(
            Path.GetTempPath(),
            "TypeWhisper.Dictionary.Tests_" + Guid.NewGuid().ToString("N")
        );
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        foreach (var models in _models)
            models.Dispose();
        _plugins.Dispose();
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, true);
            }
        }
        catch
        {
            // Best-effort cleanup for temp test directories.
        }
    }

    [Fact]
    public void AddEntry_InvalidRegex_SetsErrorAndDoesNotAdd()
    {
        var dictionary = CreateDictionaryService();
        var sut = CreateViewModel(dictionary);
        sut.NewOriginal = "[unclosed";
        sut.NewReplacement = "x";
        sut.NewIsRegex = true;
        var notifications = new List<string?>();
        sut.PropertyChanged += (_, e) => notifications.Add(e.PropertyName);

        sut.AddEntryCommand.Execute(null);

        Assert.Empty(dictionary.Entries);
        Assert.True(sut.HasRegexValidationError);
        Assert.NotEmpty(sut.RegexValidationError);
        Assert.Contains(nameof(sut.HasRegexValidationError), notifications);
    }

    [Fact]
    public void AddEntry_ValidRegex_PersistsIsRegex()
    {
        var dictionary = CreateDictionaryService();
        var sut = CreateViewModel(dictionary);
        sut.NewOriginal = "colou?r";
        sut.NewReplacement = "color";
        sut.NewIsRegex = true;

        sut.AddEntryCommand.Execute(null);

        Assert.True(Assert.Single(CreateDictionaryService().Entries).IsRegex);
        Assert.False(sut.NewIsRegex);
        Assert.False(sut.HasRegexValidationError);
    }

    [Fact]
    public void NewOriginalChange_ClearsRegexError()
    {
        var sut = CreateViewModel(CreateDictionaryService());
        sut.NewOriginal = "[unclosed";
        sut.NewIsRegex = true;
        sut.AddEntryCommand.Execute(null);
        Assert.True(sut.HasRegexValidationError);

        sut.NewOriginal = "colou?r";

        Assert.Empty(sut.RegexValidationError);
        Assert.False(sut.HasRegexValidationError);
    }

    [Fact]
    public void SwitchingToTerm_ClearsNewIsRegex()
    {
        var dictionary = CreateDictionaryService();
        var sut = CreateViewModel(dictionary);
        sut.NewOriginal = "[unclosed";
        sut.NewIsRegex = true;
        sut.AddEntryCommand.Execute(null);
        Assert.True(sut.HasRegexValidationError);

        sut.NewEntryType = DictionaryEntryType.Term;

        Assert.False(sut.NewIsRegex);
        Assert.Empty(sut.RegexValidationError);
        Assert.False(sut.HasRegexValidationError);
        sut.AddEntryCommand.Execute(null);
        Assert.False(Assert.Single(dictionary.Entries).IsRegex);
    }

    [Fact]
    public void AddEntry_PersistsPriority()
    {
        var dictionary = CreateDictionaryService();
        var sut = CreateViewModel(dictionary);

        sut.NewOriginal = "type whisper";
        sut.NewReplacement = "TypeWhisper";
        sut.NewPriority = 4;
        sut.AddEntryCommand.Execute(null);

        var entry = Assert.Single(dictionary.Entries);
        Assert.Equal(4, entry.Priority);
        Assert.Equal(0, sut.NewPriority);
    }

    [Fact]
    public void AddEntry_Correction_EnablesEscapeExpansion()
    {
        var dictionary = CreateDictionaryService();
        var sut = CreateViewModel(dictionary);
        sut.NewEntryType = DictionaryEntryType.Correction;
        sut.NewOriginal = "new paragraph";
        sut.NewReplacement = @"\n";

        sut.AddEntryCommand.Execute(null);

        Assert.True(Assert.Single(dictionary.Entries).ExpandEscapes);
    }

    [Fact]
    public void AddEntry_Term_DoesNotEnableEscapeExpansion()
    {
        var dictionary = CreateDictionaryService();
        var sut = CreateViewModel(dictionary);
        sut.NewEntryType = DictionaryEntryType.Term;
        sut.NewOriginal = "TypeWhisper";

        sut.AddEntryCommand.Execute(null);

        Assert.False(Assert.Single(dictionary.Entries).ExpandEscapes);
    }

    [Fact]
    public void EntryControls_UpdateStarredAndPriority()
    {
        var dictionary = CreateDictionaryService();
        var entry = new DictionaryEntry
        {
            Id = "entry-1",
            EntryType = DictionaryEntryType.Correction,
            Original = "wispr",
            Replacement = "Wispr",
            Priority = 1,
        };
        dictionary.AddEntry(entry);
        var sut = CreateViewModel(dictionary);

        sut.ToggleStarredCommand.Execute(entry);
        var updated = dictionary.Entries.Single();
        Assert.True(updated.IsStarred);

        sut.IncreasePriorityCommand.Execute(updated);
        updated = dictionary.Entries.Single();
        Assert.Equal(2, updated.Priority);

        sut.DecreasePriorityCommand.Execute(updated);
        updated = dictionary.Entries.Single();
        Assert.Equal(1, updated.Priority);
    }

    [Fact]
    public void Refresh_SortsStarredAndHighPriorityFirst()
    {
        var dictionary = CreateDictionaryService();
        dictionary.AddEntries([
            new DictionaryEntry
            {
                Id = "low",
                EntryType = DictionaryEntryType.Term,
                Original = "alpha",
            },
            new DictionaryEntry
            {
                Id = "priority",
                EntryType = DictionaryEntryType.Term,
                Original = "beta",
                Priority = 5,
            },
            new DictionaryEntry
            {
                Id = "starred",
                EntryType = DictionaryEntryType.Term,
                Original = "gamma",
                IsStarred = true,
            },
        ]);

        var sut = CreateViewModel(dictionary);

        Assert.Equal(
            ["gamma", "beta", "alpha"],
            sut.FilteredEntries.Select(entry => entry.Original)
        );
    }

    [Fact]
    public void ReconcileEnabledPacksFromSettings_PicksUpExternallySavedEnabledPackId()
    {
        var dictionary = CreateDictionaryService();
        var settings = new SettingsService(Path.Join(_tempDir, "settings.json"));
        var sut = CreateViewModel(dictionary, settings);
        var realEstatePack = sut.Packs.Single(p => p.Pack.Id == "real-estate");
        Assert.False(realEstatePack.IsEnabled);
        Assert.Empty(dictionary.Entries);

        settings.Save(settings.Current with { EnabledPackIds = ["real-estate"] });
        sut.ReconcileEnabledPacksFromSettings();

        Assert.True(realEstatePack.IsEnabled);
        Assert.NotEmpty(dictionary.Entries);
        Assert.All(dictionary.Entries, e => Assert.StartsWith("pack:real-estate:", e.Id));
    }

    [Fact]
    public void ReconcileEnabledPacksFromSettings_IsNoOpWhenAlreadyInSync()
    {
        var dictionary = CreateDictionaryService();
        var settings = new SettingsService(Path.Join(_tempDir, "settings.json"));
        settings.Save(settings.Current with { EnabledPackIds = ["real-estate"] });
        var sut = CreateViewModel(dictionary, settings);
        var realEstatePack = sut.Packs.Single(p => p.Pack.Id == "real-estate");
        Assert.True(realEstatePack.IsEnabled);

        sut.ReconcileEnabledPacksFromSettings();

        Assert.True(realEstatePack.IsEnabled);
    }

    [Fact]
    public void ReconcileEnabledPacksFromSettings_DeactivatesPackTermsWhenRemovedFromSettings()
    {
        var dictionary = CreateDictionaryService();
        var settings = new SettingsService(Path.Join(_tempDir, "settings.json"));
        var sut = CreateViewModel(dictionary, settings);
        settings.Save(settings.Current with { EnabledPackIds = ["real-estate"] });
        sut.ReconcileEnabledPacksFromSettings();
        var realEstatePack = sut.Packs.Single(p => p.Pack.Id == "real-estate");
        Assert.True(realEstatePack.IsEnabled);
        Assert.NotEmpty(dictionary.Entries);

        settings.Save(settings.Current with { EnabledPackIds = [] });
        sut.ReconcileEnabledPacksFromSettings();

        Assert.False(realEstatePack.IsEnabled);
        Assert.Empty(dictionary.Entries);
    }

    [Fact]
    public void AddEntry_Regex_KeepsSurroundingWhitespace()
    {
        var dictionary = CreateDictionaryService();
        var sut = CreateViewModel(dictionary);

        sut.NewOriginal = "foo ";
        sut.NewReplacement = "bar";
        sut.NewIsRegex = true;
        sut.AddEntryCommand.Execute(null);

        var entry = Assert.Single(dictionary.Entries);
        Assert.True(entry.IsRegex);
        Assert.Equal("foo ", entry.Original);
    }

    [Fact]
    public void CycleCtcSimilarity_CyclesPresetsAndIgnoresCorrections()
    {
        var dictionary = CreateDictionaryService();
        dictionary.AddEntry(
            new DictionaryEntry
            {
                Id = "term",
                Original = "term",
                EntryType = DictionaryEntryType.Term,
            }
        );
        var sut = CreateViewModel(dictionary);
        foreach (var expected in new float?[] { .5f, .65f, .8f, null })
        {
            sut.CycleCtcSimilarityCommand.Execute(dictionary.Entries[0]);
            Assert.Equal(expected, dictionary.Entries[0].CtcMinSimilarity);
        }
        dictionary.AddEntry(
            new DictionaryEntry
            {
                Id = "correction",
                Original = "typo",
                Replacement = "fixed",
                EntryType = DictionaryEntryType.Correction,
            }
        );
        var correction = dictionary.Entries[1];
        sut.CycleCtcSimilarityCommand.Execute(correction);
        Assert.Equal(correction, dictionary.Entries[1]);
    }

    [Theory]
    [InlineData(.4f, .5f)]
    [InlineData(.6f, .65f)]
    [InlineData(.7f, .8f)]
    [InlineData(.95f, null)]
    public void CycleCtcSimilarity_CustomAdvancesToNextPreset(float current, float? expected)
    {
        var dictionary = CreateDictionaryService();
        dictionary.AddEntry(
            new DictionaryEntry
            {
                Id = "term",
                Original = "term",
                EntryType = DictionaryEntryType.Term,
                CtcMinSimilarity = current,
            }
        );
        CreateViewModel(dictionary).CycleCtcSimilarityCommand.Execute(dictionary.Entries[0]);
        Assert.Equal(expected, dictionary.Entries[0].CtcMinSimilarity);
    }

    [Theory]
    [InlineData(null, "Dictionary.CtcSimilarityAuto")]
    [InlineData(.5f, "Dictionary.CtcSimilarityStrong")]
    [InlineData(.65f, "Dictionary.CtcSimilarityBalanced")]
    [InlineData(.8f, "Dictionary.CtcSimilarityPrecise")]
    public void DescribeCtcSimilarity_UsesLocalizedPreset(float? value, string key) =>
        Assert.Equal(Loc.Instance[key], DictionarySectionViewModel.DescribeCtcSimilarity(value));

    [Fact]
    public void DescribeCtcSimilarity_FormatsCustomPercentage() =>
        Assert.Equal(
            Loc.Instance.GetString("Dictionary.CtcSimilarityCustom", "73"),
            DictionarySectionViewModel.DescribeCtcSimilarity(.734f)
        );

    [Fact]
    public void AcousticToggle_PersistsOnlyChanges()
    {
        var settings = new SettingsService(Path.Join(_tempDir, "settings.json"));
        var sut = CreateViewModel(CreateDictionaryService(), settings);
        var initial = settings.Current.AcousticVocabularyBoostingEnabled;
        Assert.Equal(initial, sut.AcousticVocabularyBoostingEnabled);
        var writes = 0;
        settings.SettingsChanged += _ => writes++;
        sut.AcousticVocabularyBoostingEnabled = !initial;
        Assert.Equal(!initial, settings.Current.AcousticVocabularyBoostingEnabled);
        sut.AcousticVocabularyBoostingEnabled = !initial;
        Assert.Equal(1, writes);
        // The underlying setting may change before the UI reconciles it.
        settings.Update(current => current with { AcousticVocabularyBoostingEnabled = initial });
        sut.AcousticVocabularyBoostingEnabled = initial;
        Assert.Equal(2, writes);
    }

    [Fact]
    public void AcousticStatus_ReportsPluginEngineTermsAndReady()
    {
        var dictionary = CreateDictionaryService();
        var sut = CreateViewModel(dictionary);
        Assert.Equal(
            Loc.Instance["Dictionary.AcousticBoostingStatusNoPlugin"],
            sut.AcousticBoostingStatusText
        );
        PluginManagerTestAccess.SetVocabularyRescorers(
            _plugins,
            [new FakeVocabularyRescorerPlugin()]
        );
        Assert.Equal(
            Loc.Instance.GetString(
                "Dictionary.AcousticBoostingStatusEngine",
                Loc.Instance["Dictation.NoModelLoaded"]
            ),
            sut.AcousticBoostingStatusText
        );
        var engine = new Mock<ITranscriptionEngineRole>();
        engine.SetupGet(e => e.PluginId).Returns("sherpa");
        engine.SetupGet(e => e.ProviderId).Returns("sherpa-onnx");
        engine.SetupGet(e => e.SelectedModelId).Returns("parakeet-tdt-0.6b");
        PluginManagerTestAccess.SetTranscriptionEngines(_plugins, [engine.Object]);
        typeof(ModelManagerService)
            .GetProperty(nameof(ModelManagerService.ActiveModelId))!
            .SetValue(
                _models.Single(),
                ModelManagerService.GetPluginModelId("sherpa", "parakeet-tdt-0.6b")
            );
        Assert.Equal(
            Loc.Instance["Dictionary.AcousticBoostingStatusNoTerms"],
            sut.AcousticBoostingStatusText
        );
        dictionary.AddEntry(
            new DictionaryEntry
            {
                Id = "term",
                Original = "term",
                EntryType = DictionaryEntryType.Term,
            }
        );
        Assert.Equal(
            Loc.Instance["Dictionary.AcousticBoostingStatusActive"],
            sut.AcousticBoostingStatusText
        );
        sut.AcousticVocabularyBoostingEnabled = false;
        Assert.Equal(Loc.Instance["Common.Disabled"], sut.AcousticBoostingStatusText);
    }

    private DictionaryService CreateDictionaryService()
    {
        return new DictionaryService(Path.Join(_tempDir, "dictionary.json"));
    }

    private DictionarySectionViewModel CreateViewModel(
        DictionaryService dictionary,
        ISettingsService? settings = null
    )
    {
        settings ??= new SettingsService(Path.Join(_tempDir, "settings.json"));
        var models = new ModelManagerService(_plugins, settings);
        _models.Add(models);
        return new DictionarySectionViewModel(
            dictionary,
            settings,
            models,
            new VocabularyRescoringService(_plugins, settings, dictionary, models)
        );
    }
}
