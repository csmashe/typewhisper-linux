using TypeWhisper.Core.Models;
using TypeWhisper.Core.Services;

namespace TypeWhisper.Core.Tests.Services;

/// <summary>Covers <see cref="SnippetService" />: trigger expansion, placeholders, tags, profile scoping, and JSON import/export.</summary>
public sealed class SnippetServiceTests : IDisposable
{
    private static readonly TimeSpan s_frozenOffset = TimeSpan.FromHours(2);

    // Just past local midnight, so the UTC instant is still 2039-12-31 and the local-field
    // placeholders ({date}, {day}, {year}) differ from their UTC equivalents. A same-date
    // instant could not tell the two apart.
    private static readonly DateTimeOffset s_frozenLocalNow = new(
        2040,
        1,
        1,
        0,
        30,
        0,
        s_frozenOffset
    );
    private static readonly TimeZoneInfo s_frozenTimeZone = TimeZoneInfo.CreateCustomTimeZone(
        "SnippetServiceTests/UTC+02",
        s_frozenOffset,
        "SnippetServiceTests/UTC+02",
        "SnippetServiceTests/UTC+02"
    );

    private readonly string _filePath;
    private readonly FixedTimeProvider _timeProvider;
    private readonly SnippetService _sut;

    public SnippetServiceTests()
    {
        _filePath = Path.GetTempFileName();
        _timeProvider = new FixedTimeProvider(
            s_frozenLocalNow.ToUniversalTime(),
            s_frozenTimeZone
        );
        _sut = new SnippetService(_filePath, _timeProvider);
    }

    public void Dispose()
    {
        if (File.Exists(_filePath))
        {
            File.Delete(_filePath);
        }
    }

    [Fact]
    public void AddSnippet_WithTags_PersistsAndLoads()
    {
        _sut.AddSnippet(
            new Snippet
            {
                Id = "1",
                Trigger = "mfg",
                Replacement = "Mit freundlichen Grüßen",
                Tags = "E-Mail,Gruß",
            }
        );

        var freshService = new SnippetService(_filePath);
        var snippet = Assert.Single(freshService.Snippets);
        Assert.Equal("E-Mail,Gruß", snippet.Tags);
    }

    [Fact]
    public void ApplySnippets_ClipboardPlaceholder_ExpandsFromProvider()
    {
        _sut.AddSnippet(
            new Snippet
            {
                Id = "1",
                Trigger = "link",
                Replacement = "Siehe: {clipboard}",
            }
        );

        var result = _sut.ApplySnippets("link", () => "https://example.com");
        Assert.Equal("Siehe: https://example.com", result);
    }

    [Fact]
    public void ApplySnippets_ClipboardPlaceholder_EmptyWhenNoProvider()
    {
        _sut.AddSnippet(
            new Snippet
            {
                Id = "1",
                Trigger = "link",
                Replacement = "Siehe: {clipboard}",
            }
        );

        var result = _sut.ApplySnippets("link");
        Assert.Equal("Siehe: ", result);
    }

    [Fact]
    public void ApplySnippets_CustomDateFormat_ExpandsCorrectly()
    {
        _sut.AddSnippet(
            new Snippet
            {
                Id = "1",
                Trigger = "heute",
                Replacement = "{date:dd.MM.yyyy}",
            }
        );

        var result = _sut.ApplySnippets("heute");
        Assert.Equal(s_frozenLocalNow.ToString("dd.MM.yyyy"), result);
    }

    [Fact]
    public void ApplySnippets_CustomTimeFormat_ExpandsCorrectly()
    {
        _sut.AddSnippet(
            new Snippet
            {
                Id = "1",
                Trigger = "uhr",
                Replacement = "{time:HH:mm:ss}",
            }
        );

        var result = _sut.ApplySnippets("uhr");
        var expected = s_frozenLocalNow.ToString("HH:mm:ss");
        Assert.Equal(expected, result);
    }

    [Fact]
    public void ApplySnippets_StandardPlaceholders_StillWork()
    {
        _sut.AddSnippet(
            new Snippet
            {
                Id = "1",
                Trigger = "datum",
                Replacement = "{date}",
            }
        );
        _sut.AddSnippet(
            new Snippet
            {
                Id = "2",
                Trigger = "zeit",
                Replacement = "{time}",
            }
        );
        _sut.AddSnippet(
            new Snippet
            {
                Id = "3",
                Trigger = "tag",
                Replacement = "{day}",
            }
        );
        _sut.AddSnippet(
            new Snippet
            {
                Id = "4",
                Trigger = "jahr",
                Replacement = "{year}",
            }
        );

        Assert.Equal(
            s_frozenLocalNow.ToString("yyyy-MM-dd"),
            _sut.ApplySnippets("datum")
        );
        Assert.Equal(s_frozenLocalNow.ToString("HH:mm"), _sut.ApplySnippets("zeit"));
        Assert.Equal(s_frozenLocalNow.ToString("dddd"), _sut.ApplySnippets("tag"));
        Assert.Equal(s_frozenLocalNow.Year.ToString(), _sut.ApplySnippets("jahr"));
    }

    [Fact]
    public void PreviewReplacement_ExpandsPlaceholdersWithoutSnippetTrigger()
    {
        var result = _sut.PreviewReplacement(
            "Today is {date:yyyy-MM-dd}; clipboard={clipboard}",
            () => "copied"
        );

        Assert.Equal(
            $"Today is {s_frozenLocalNow:yyyy-MM-dd}; clipboard=copied",
            result
        );
    }

    [Fact]
    public void ApplySnippets_MultipleDateTimePlaceholders_UseSingleFrozenInstant()
    {
        _sut.AddSnippet(
            new Snippet
            {
                Id = "1",
                Trigger = "boundary",
                Replacement =
                    "{date:yyyy-MM-dd}|{time:HH:mm:ss}|{datetime:yyyy-MM-dd HH:mm:ss}|{day}|{year}",
            }
        );

        var result = _sut.ApplySnippets("boundary");

        Assert.Equal(
            $"{s_frozenLocalNow:yyyy-MM-dd}|{s_frozenLocalNow:HH:mm:ss}|"
                + $"{s_frozenLocalNow:yyyy-MM-dd HH:mm:ss}|{s_frozenLocalNow:dddd}|"
                + s_frozenLocalNow.Year,
            result
        );
        // Two reads, not five: one shared instant for every placeholder in the expansion,
        // plus the LastUsedAt stamp. Per-placeholder reads would show up as six.
        Assert.Equal(2, _timeProvider.UtcNowReadCount);
    }

    [Fact]
    public void ApplySnippets_OffsetBearingFormats_EmitLocalUtcOffset()
    {
        _sut.AddSnippet(
            new Snippet
            {
                Id = "1",
                Trigger = "offset",
                Replacement = "{datetime:O}|{datetime:yyyy-MM-dd HH:mm:sszzz}",
            }
        );

        var result = _sut.ApplySnippets("offset");

        Assert.Equal(
            $"{s_frozenLocalNow:O}|{s_frozenLocalNow:yyyy-MM-dd HH:mm:sszzz}",
            result
        );
    }

    [Fact]
    public void ApplySnippets_UniversalFullDateTimeFormat_FormatsAsUtc()
    {
        _sut.AddSnippet(
            new Snippet
            {
                Id = "1",
                Trigger = "universal",
                Replacement = "{datetime:U}",
            }
        );

        var result = _sut.ApplySnippets("universal");

        Assert.Equal(s_frozenLocalNow.UtcDateTime.ToString("U"), result);
    }

    [Theory]
    // The Z/GMT suffix is the format's, not a conversion: these are the local fields
    // (2040-01-01 00:30), whereas the UTC instant is 2039-12-31 22:30.
    [InlineData("u", "2040-01-01 00:30:00Z")]
    [InlineData("R", "Sun, 01 Jan 2040 00:30:00 GMT")]
    [InlineData("r", "Sun, 01 Jan 2040 00:30:00 GMT")]
    public void ApplySnippets_WallClockStandardFormats_KeepLocalFields(
        string format,
        string expected
    )
    {
        _sut.AddSnippet(
            new Snippet
            {
                Id = "1",
                Trigger = "legacy",
                Replacement = $"{{datetime:{format}}}",
            }
        );

        Assert.Equal(expected, _sut.ApplySnippets("legacy"));
    }

    [Fact]
    public void AllTags_ReturnsDistinctSortedTags()
    {
        _sut.AddSnippet(
            new Snippet
            {
                Id = "1",
                Trigger = "a",
                Replacement = "A",
                Tags = "Code,E-Mail",
            }
        );
        _sut.AddSnippet(
            new Snippet
            {
                Id = "2",
                Trigger = "b",
                Replacement = "B",
                Tags = "E-Mail,Datum",
            }
        );
        _sut.AddSnippet(
            new Snippet
            {
                Id = "3",
                Trigger = "c",
                Replacement = "C",
                Tags = "",
            }
        );

        var tags = _sut.AllTags;
        Assert.Equal(3, tags.Count);
        Assert.Equal("Code", tags[0]);
        Assert.Equal("Datum", tags[1]);
        Assert.Equal("E-Mail", tags[2]);
    }

    [Fact]
    public void ExportToJson_ReturnsValidJson()
    {
        _sut.AddSnippet(
            new Snippet
            {
                Id = "1",
                Trigger = "mfg",
                Replacement = "Grüße",
                Tags = "E-Mail",
            }
        );
        _sut.AddSnippet(
            new Snippet
            {
                Id = "2",
                Trigger = "sig",
                Replacement = "Signatur\nZeile 2",
            }
        );

        var json = _sut.ExportToJson();

        Assert.Contains("mfg", json);
        Assert.Contains("sig", json);
        Assert.Contains("E-Mail", json);
    }

    [Fact]
    public void ImportFromJson_AddsSnippets()
    {
        _sut.AddSnippet(
            new Snippet
            {
                Id = "1",
                Trigger = "existing",
                Replacement = "Existing",
            }
        );

        const string json = """
                            [
                                {"Id":"x","Trigger":"neu","Replacement":"Neuer Snippet","CaseSensitive":false,"IsEnabled":true,"UsageCount":0,"Tags":"Import","CreatedAt":"2026-01-01T00:00:00"}
                            ]
                            """;

        var count = _sut.ImportFromJson(json);
        Assert.Equal(1, count);
        Assert.Equal(2, _sut.Snippets.Count);
        Assert.Contains(_sut.Snippets, s => s.Trigger == "neu");
    }

    [Fact]
    public void ImportFromJson_SkipsDuplicateTriggers()
    {
        _sut.AddSnippet(
            new Snippet
            {
                Id = "1",
                Trigger = "mfg",
                Replacement = "Grüße",
            }
        );

        const string json = """
                            [
                                {"Id":"x","Trigger":"mfg","Replacement":"Anderer Text","CaseSensitive":false,"IsEnabled":true,"UsageCount":0,"Tags":"","CreatedAt":"2026-01-01T00:00:00"},
                                {"Id":"y","Trigger":"neu","Replacement":"Neuer Text","CaseSensitive":false,"IsEnabled":true,"UsageCount":0,"Tags":"","CreatedAt":"2026-01-01T00:00:00"}
                            ]
                            """;

        var count = _sut.ImportFromJson(json);
        Assert.Equal(1, count); // only "neu" imported, "mfg" skipped
        Assert.Equal(2, _sut.Snippets.Count);
    }

    [Fact]
    public void ApplySnippets_MultilineReplacement_Works()
    {
        _sut.AddSnippet(
            new Snippet
            {
                Id = "1",
                Trigger = "sig",
                Replacement = "Mit freundlichen Grüßen\nMarco Mustermann\nTypeWhisper GmbH",
            }
        );

        var result = _sut.ApplySnippets("sig");
        Assert.Equal("Mit freundlichen Grüßen\nMarco Mustermann\nTypeWhisper GmbH", result);
    }

    [Theory]
    [InlineData("mfg.", "Mit freundlichen Grüßen")]
    [InlineData("mfg!", "Mit freundlichen Grüßen")]
    [InlineData("mfg?", "Mit freundlichen Grüßen")]
    [InlineData("mfg", "Mit freundlichen Grüßen")]
    [InlineData("Sage mfg. bitte", "Sage Mit freundlichen Grüßen bitte")]
    public void ApplySnippets_ConsumesTrailingPunctuation(string input, string expected)
    {
        _sut.AddSnippet(
            new Snippet
            {
                Id = "1",
                Trigger = "mfg",
                Replacement = "Mit freundlichen Grüßen",
            }
        );

        var result = _sut.ApplySnippets(input);
        Assert.Equal(expected, result);
    }

    [Fact]
    public void ApplySnippets_ExactPhraseTrigger_ReplacesWholeUtteranceOnly()
    {
        _sut.AddSnippet(
            new Snippet
            {
                Id = "1",
                Trigger = "sig",
                Replacement = "Signature",
                TriggerMode = SnippetTriggerMode.ExactPhrase,
            }
        );

        Assert.Equal("Signature", _sut.ApplySnippets("sig."));
        Assert.Equal("please use sig", _sut.ApplySnippets("please use sig"));
    }

    [Theory]
    [InlineData("  sig!  ", "Signature")]
    [InlineData("SIG?", "Signature")]
    [InlineData("sig\n", "Signature")]
    [InlineData("sig..", "sig..")]
    [InlineData("sig sig", "sig sig")]
    [InlineData("signature", "signature")]
    public void ApplySnippets_ExactPhraseTrigger_MatchesOnlyTheWholeTrimmedUtterance(string input, string expected)
    {
        _sut.AddSnippet(
            new Snippet
            {
                Id = "1",
                Trigger = "sig",
                Replacement = "Signature",
                TriggerMode = SnippetTriggerMode.ExactPhrase,
            }
        );

        Assert.Equal(expected, _sut.ApplySnippets(input));
    }

    [Fact]
    public void ApplySnippets_ExactPhraseTrigger_YieldsToLongerAnywhereMatch()
    {
        _sut.AddSnippet(
            new Snippet
            {
                Id = "exact",
                Trigger = "sig",
                Replacement = "Exact",
                TriggerMode = SnippetTriggerMode.ExactPhrase,
            }
        );
        _sut.AddSnippet(new Snippet { Id = "longer", Trigger = "sig.", Replacement = "Longer" });

        Assert.Equal("Longer", _sut.ApplySnippets("sig."));
        Assert.Equal(0, _sut.Snippets.Single(s => s.Id == "exact").UsageCount);
    }

    [Theory]
    [InlineData("btw", "btw", "expanded")]
    [InlineData("btw", "BTW, please", "expanded, please")]
    [InlineData("btw", "(btw)\nbtw!", "(expanded)\nexpanded")]
    [InlineData("btw", "btw. btw? btw!", "expanded expanded expanded")]
    [InlineData("btw", "abtw btwx abtwx btw", "abtw btwx abtwx expanded")]
    [InlineData(";sig", ";sig", "expanded")]
    [InlineData(";sig", "Please ;sig.", "Please expanded")]
    [InlineData("c++", "(c++)", "(expanded)")]
    [InlineData("[sig]", "[sig]!", "expanded")]
    [InlineData("backslash sig", "Please backslash sig.", "Please expanded")]
    [InlineData("btw", "😀btw😀", "😀expanded😀")]
    [InlineData("谢谢", "非常谢谢你", "非常expanded你")]
    [InlineData("ありがとう", "本当にありがとうございます", "本当にexpandedございます")]
    [InlineData("サイン", "ここにサインしてください", "ここにexpandedしてください")]
    [InlineData("감사", "감사합니다", "expanded합니다")]
    [InlineData("𠀀", "𠀁𠀀𠀂", "𠀁expanded𠀂")]
    [InlineData("foo谢谢", "foo谢谢你", "expanded你")]
    [InlineData("谢谢bar", "非常谢谢bar", "非常expanded")]
    [InlineData("ขอบคุณ", "ขอบคุณครับ", "expandedครับ")]
    [InlineData("สวัสดี", "สวัสดีครับ", "expandedครับ")]
    [InlineData("ຂອບໃຈ", "ຂອບໃຈຫຼາຍ", "expandedຫຼາຍ")]
    [InlineData("អរគុណ", "អរគុណច្រើន", "expandedច្រើន")]
    [InlineData("ကျေးဇူး", "ကျေးဇူးတင်ပါတယ်", "expandedတင်ပါတယ်")]
    [InlineData("fooขอบคุณ", "fooขอบคุณครับ", "expandedครับ")]
    [InlineData("email", "我的email是", "我的expanded是")]
    [InlineData("btw", "メールはbtwです", "メールはexpandedです")]
    [InlineData("email", "제email은", "제expanded은")]
    [InlineData("btw", "ขอบคุณbtwครับ", "ขอบคุณexpandedครับ")]
    [InlineData("btw", "'btw' ‘btw’", "'expanded' ‘expanded’")]
    [InlineData("btw", "''btw''", "''expanded''")]
    [InlineData("btw", "\u200Ebtw\u200F", "\u200Eexpanded\u200F")]
    [InlineData("btw", "a\u200Bbtw\u200Bx", "a\u200Bexpanded\u200Bx")]
    [InlineData("👍🏽", "👍🏽", "expanded")]
    [InlineData("฿sig", "฿sig", "expanded")]
    [InlineData("café", "un café, merci", "un expanded, merci")]
    [InlineData("cafe\u0301", "un cafe\u0301!", "un expanded")]
    public void ApplySnippets_StandaloneTriggers_Expand(string trigger, string input, string expected)
    {
        _sut.AddSnippet(new Snippet { Id = "1", Trigger = trigger, Replacement = "expanded" });

        Assert.Equal(expected, _sut.ApplySnippets(input));
        Assert.Equal(1, _sut.Snippets[0].UsageCount);
    }

    [Theory]
    [InlineData("btw", "abtw")]
    [InlineData("btw", "btwx")]
    [InlineData("btw", "abtwx")]
    [InlineData("btw", "1btw btw2 _btw btw_")]
    [InlineData("btw", "äbtw btwß")]
    [InlineData("btw", "btw\u0301")]
    [InlineData("क", "का")]
    [InlineData("btw", "btw\u20DD")]
    [InlineData("btw", "𐐀btw")]
    [InlineData("btw", "btw𐐀")]
    [InlineData("btw", "btw\U0001D165")]
    [InlineData("btw", "\U0001D7D8btw")]
    [InlineData("btw", "Ⅲbtw btw²")]
    [InlineData("foo谢谢bar", "xfoo谢谢bary")]
    [InlineData("foo谢谢", "xfoo谢谢你")]
    [InlineData("谢谢bar", "非常谢谢bary")]
    [InlineData("fooขอบคุณ", "xfooขอบคุณครับ")]
    [InlineData("btw", "我abtw btwx是")]
    [InlineData("btw", "๑btw btw๒")]
    [InlineData("ขอบคุณbar", "ขอบคุณbary")]
    [InlineData("๑", "๑๒")]
    [InlineData("ها", "کتاب\u200Cها")]
    [InlineData("btw", "a\u200Dbtw btw\u200Dx")]
    [InlineData("btw", "a\u200Cbtw btw\u200Cx")]
    [InlineData("can", "can't can’t")]
    [InlineData("re", "we're we’re")]
    [InlineData("can", "can'𐐀")]
    [InlineData("ware", "soft\u00ADware")]
    [InlineData("soft", "soft\u00ADware")]
    [InlineData("btw", "a\u200E\u2060btw btw\u2060\u200Fx")]
    [InlineData("צה", "צה״ל")]
    [InlineData("ל", "צה״ל")]
    [InlineData("ג", "ג׳")]
    [InlineData("฿sig", "a฿sig")]
    [InlineData("sig฿", "sig฿x")]
    [InlineData("👍", "👍🏽")]
    [InlineData("👩", "👩\u200D💻")]
    [InlineData("か", "か\u3099")]
    [InlineData(";sig", "a;sig ;signature")]
    [InlineData("c++", "abc++ c++17")]
    [InlineData("[sig]", "sig")]
    [InlineData("backslash sig", "backslash signature")]
    [InlineData("backslash sig", "abackslash sig")]
    [InlineData("link", "hyperlink links")]
    [InlineData("", "normal text")]
    public void ApplySnippets_NonMatchingTriggers_DoNotExpandOrCountUsage(string trigger, string input)
    {
        _sut.AddSnippet(new Snippet { Id = "1", Trigger = trigger, Replacement = "{clipboard}" });
        var clipboardReads = 0;

        var result = _sut.ApplySnippets(
            input,
            () =>
            {
                clipboardReads++;
                return "expanded";
            }
        );

        Assert.Equal(input, result);
        Assert.Equal(0, clipboardReads);
        Assert.Equal(0, _sut.Snippets[0].UsageCount);
        Assert.Equal(0, new SnippetService(_filePath).Snippets[0].UsageCount);
    }

    [Theory]
    [InlineData(false, "expanded expanded abtw")]
    [InlineData(true, "BTW expanded abtw")]
    public void ApplySnippets_RespectsCaseSensitivityAtWordBoundaries(bool caseSensitive, string expected)
    {
        _sut.AddSnippet(
            new Snippet
            {
                Id = "1",
                Trigger = "btw",
                Replacement = "expanded",
                CaseSensitive = caseSensitive,
            }
        );

        Assert.Equal(expected, _sut.ApplySnippets("BTW btw abtw"));
    }

    [Fact]
    public void ApplySnippets_AdjacentTriggers_UseOriginalBoundaries()
    {
        _sut.AddSnippet(new Snippet { Id = "1", Trigger = "hello", Replacement = "Hi" });
        _sut.AddSnippet(new Snippet { Id = "2", Trigger = "btw", Replacement = "aside" });

        Assert.Equal("Hiaside asideHi", _sut.ApplySnippets("hello.btw btw!hello"));
        Assert.All(_sut.Snippets, snippet => Assert.Equal(1, snippet.UsageCount));
    }

    [Fact]
    public void ApplySnippets_OverlappingTriggers_PreferLongestAndDoNotReprocessReplacement()
    {
        _sut.AddSnippet(new Snippet { Id = "1", Trigger = "my signature", Replacement = "sig" });
        _sut.AddSnippet(new Snippet { Id = "2", Trigger = "signature", Replacement = "{clipboard}" });
        _sut.AddSnippet(new Snippet { Id = "3", Trigger = "sig", Replacement = "Expanded" });
        var clipboardReads = 0;

        var result = _sut.ApplySnippets(
            "my signature sig",
            () =>
            {
                clipboardReads++;
                return "unexpected";
            }
        );

        Assert.Equal("sig Expanded", result);
        Assert.Equal(0, clipboardReads);
        Assert.Equal(0, _sut.Snippets[1].UsageCount);
    }

    [Fact]
    public void ApplySnippets_ReplacementText_IsNotExpandedByAnotherSnippet()
    {
        _sut.AddSnippet(new Snippet { Id = "1", Trigger = "alpha", Replacement = "beta" });
        _sut.AddSnippet(new Snippet { Id = "2", Trigger = "beta", Replacement = "expanded" });

        Assert.Equal("beta", _sut.ApplySnippets("alpha"));
        Assert.Equal("beta expanded", _sut.ApplySnippets("alpha beta"));
    }

    [Fact]
    public void ApplySnippets_OverlapRejection_DoesNotSkipLaterOccurrence()
    {
        _sut.AddSnippet(new Snippet { Id = "1", Trigger = "a b c", Replacement = "X" });
        _sut.AddSnippet(new Snippet { Id = "2", Trigger = "c d", Replacement = "Y" });

        // "c d" first overlaps the claimed "a b c"; its second occurrence still expands.
        Assert.Equal("X d Y", _sut.ApplySnippets("a b c d c d"));
    }

    [Fact]
    public void ApplySnippets_MultipleOccurrences_ShareOneExpansionAndOneUsageIncrement()
    {
        _sut.AddSnippet(new Snippet { Id = "1", Trigger = "paste", Replacement = "[{clipboard}]" });
        var clipboardReads = 0;

        var result = _sut.ApplySnippets(
            "paste and paste",
            () =>
            {
                clipboardReads++;
                return "clip";
            }
        );

        Assert.Equal("[clip] and [clip]", result);
        Assert.Equal(1, clipboardReads);
        Assert.Equal(1, _sut.Snippets[0].UsageCount);
    }

    [Theory]
    [InlineData("$1 off", "Now $1 off today")]
    [InlineData("$&$$", "Now $&$$ today")]
    [InlineData("{clipboard}", "Now $0 today")]
    public void ApplySnippets_DollarSigns_ArePreservedLiterally(string replacement, string expected)
    {
        _sut.AddSnippet(new Snippet { Id = "1", Trigger = "price", Replacement = replacement });

        Assert.Equal(expected, _sut.ApplySnippets("Now price today", () => "$0"));
    }

    [Fact]
    public void ApplySnippets_ProfileScopedSnippet_OnlyAppliesToMatchingProfile()
    {
        _sut.AddSnippet(
            new Snippet
            {
                Id = "1",
                Trigger = "sig",
                Replacement = "Profile signature",
                ProfileIds = ["profile-1"],
            }
        );

        Assert.Equal("sig", _sut.ApplySnippets("sig"));
        Assert.Equal("sig", _sut.ApplySnippets("sig", profileId: "profile-2"));
        Assert.Equal("Profile signature", _sut.ApplySnippets("sig", profileId: "profile-1"));
    }

    [Fact]
    public void ApplySnippets_GlobalSnippet_AppliesWhenProfileIsActive()
    {
        _sut.AddSnippet(
            new Snippet
            {
                Id = "1",
                Trigger = "sig",
                Replacement = "Global signature",
            }
        );

        Assert.Equal("Global signature", _sut.ApplySnippets("sig", profileId: "profile-1"));
    }

    [Fact]
    public void ApplySnippets_UpdatesLastUsedAt()
    {
        _sut.AddSnippet(
            new Snippet
            {
                Id = "1",
                Trigger = "sig",
                Replacement = "Signature",
            }
        );

        _sut.ApplySnippets("sig");

        Assert.Equal(1, _sut.Snippets[0].UsageCount);
        Assert.NotNull(_sut.Snippets[0].LastUsedAt);
    }

    [Fact]
    public void Snippets_LoadLegacyJsonWithTriggerModeDefaults()
    {
        File.WriteAllText(
            _filePath,
            """
            [
              {
                "Id": "legacy",
                "Trigger": "sig",
                "Replacement": "Signature",
                "IsEnabled": true
              }
            ]
            """
        );

        var sut = new SnippetService(_filePath);

        var snippet = Assert.Single(sut.Snippets);
        Assert.Equal(SnippetTriggerMode.Anywhere, snippet.TriggerMode);
        Assert.Null(snippet.LastUsedAt);
    }

    [Fact]
    public void UpdateSnippet_WithTags_PersistsChanges()
    {
        _sut.AddSnippet(
            new Snippet
            {
                Id = "1",
                Trigger = "mfg",
                Replacement = "Grüße",
                Tags = "Alt",
            }
        );
        _sut.UpdateSnippet(
            new Snippet
            {
                Id = "1",
                Trigger = "mfg",
                Replacement = "Grüße",
                Tags = "Neu",
            }
        );

        var freshService = new SnippetService(_filePath);
        Assert.Equal("Neu", freshService.Snippets[0].Tags);
    }

    [Fact]
    public void AddSnippet_WhenSaveFails_ThrowsWithoutChangingCacheFileOrEvent()
    {
        const string originalJson =
            "[{\"Id\":\"old\",\"Trigger\":\"old\",\"Replacement\":\"Keep this\",\"IsEnabled\":true}]";
        using var failurePath = new AtomicWriteFailureTestPath(originalJson);
        var sut = new SnippetService(failurePath.FilePath);
        var original = Assert.Single(sut.Snippets);
        var before = File.ReadAllBytes(failurePath.FilePath);
        var eventFired = false;
        sut.SnippetsChanged += () => eventFired = true;

        Assert.ThrowsAny<Exception>(() =>
            sut.AddSnippet(
                new Snippet
                {
                    Id = "new",
                    Trigger = "new",
                    Replacement = "Do not persist",
                }
            )
        );

        Assert.Equal(original, Assert.Single(sut.Snippets));
        Assert.False(eventFired);
        Assert.Equal(before, File.ReadAllBytes(failurePath.FilePath));
        Assert.Empty(failurePath.TemporaryFiles);
    }

    [Fact]
    public void ImportFromJson_WhenSaveFails_ThrowsWithoutChangingCacheFileOrEvent()
    {
        const string originalJson =
            "[{\"Id\":\"old\",\"Trigger\":\"old\",\"Replacement\":\"Keep this\",\"IsEnabled\":true}]";
        const string importedJson =
            "[{\"Id\":\"imported\",\"Trigger\":\"new\",\"Replacement\":\"Do not persist\",\"IsEnabled\":true}]";
        using var failurePath = new AtomicWriteFailureTestPath(originalJson);
        var sut = new SnippetService(failurePath.FilePath);
        var original = Assert.Single(sut.Snippets);
        var before = File.ReadAllBytes(failurePath.FilePath);
        var eventFired = false;
        sut.SnippetsChanged += () => eventFired = true;

        Assert.ThrowsAny<Exception>(() => sut.ImportFromJson(importedJson));

        Assert.Equal(original, Assert.Single(sut.Snippets));
        Assert.False(eventFired);
        Assert.Equal(before, File.ReadAllBytes(failurePath.FilePath));
        Assert.Empty(failurePath.TemporaryFiles);
    }

    [Fact]
    public void ApplySnippets_WhenUsageSaveFails_DoesNotThrowAndRollsBackCache()
    {
        const string originalJson =
            "[{\"Id\":\"old\",\"Trigger\":\"old\",\"Replacement\":\"Expanded\",\"IsEnabled\":true}]";
        using var failurePath = new AtomicWriteFailureTestPath(originalJson);
        var sut = new SnippetService(failurePath.FilePath);
        _ = Assert.Single(sut.Snippets);
        var before = File.ReadAllBytes(failurePath.FilePath);

        var exception = Record.Exception(() => sut.ApplySnippets("old"));

        Assert.Null(exception);
        var updated = Assert.Single(sut.Snippets);
        Assert.Equal(0, updated.UsageCount);
        Assert.Null(updated.LastUsedAt);
        Assert.Equal(before, File.ReadAllBytes(failurePath.FilePath));
        Assert.Empty(failurePath.TemporaryFiles);
    }

    private sealed class FixedTimeProvider : TimeProvider
    {
        private readonly DateTimeOffset _utcNow;

        public FixedTimeProvider(DateTimeOffset utcNow, TimeZoneInfo localTimeZone)
        {
            _utcNow = utcNow;
            LocalTimeZone = localTimeZone;
        }

        public int UtcNowReadCount { get; private set; }

        public override TimeZoneInfo LocalTimeZone { get; }

        public override DateTimeOffset GetUtcNow()
        {
            UtcNowReadCount++;
            return _utcNow;
        }
    }
}
