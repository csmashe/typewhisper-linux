using TypeWhisper.Core.Models;
using TypeWhisper.Core.Services;
using TypeWhisper.Linux.Services;
using TypeWhisper.Linux.Services.ActiveWindow;
using TypeWhisper.PluginSDK.Processes;
using Xunit;

namespace TypeWhisper.Linux.Tests;

/// <summary>
///     Drives <see cref="AtSpiUrlExtractor" /> through recorded busctl output. busctl prints
///     non-ASCII UTF-8 as octal byte escapes, so an IDN host in the address bar must come back
///     as Unicode, not one Latin-1 char per byte.
/// </summary>
public sealed class AtSpiUrlExtractorBusctlTests : IDisposable
{
    private const string AppPath = "/org/a11y/atspi/accessible/app";
    private const string FramePath = "/org/a11y/atspi/accessible/frame";
    private const string EntryPath = "/org/a11y/atspi/accessible/entry";
    private const string DocumentPath = "/org/a11y/atspi/accessible/document";

    // Role word 2 with only bit 31 (document web, role 95) set.
    private const string DocumentRoleWord = "-2147483648";

    private readonly string _profilesPath = Path.GetTempFileName();

    public void Dispose()
    {
        File.Delete(_profilesPath);
    }

    [Theory]
    // Strings exactly as busctl 259 printed them from a live D-Bus service.
    [InlineData(@"b\303\274cher.test:18212/qa", "bücher.test:18212/qa")]
    [InlineData(@"\346\227\245\346\234\254 \342\234\223", "日本 ✓")]
    [InlineData(@"\""x\\y\"" tab\tend", "\"x\\y\" tab\tend")]
    [InlineData(@"\\303", @"\303")]
    [InlineData("example.com/a", "example.com/a")]
    [InlineData("bücher.test", "bücher.test")]
    public void UnescapeBusctlString_DecodesByteEscapesAsUtf8(string escaped, string expected)
    {
        Assert.Equal(expected, AtSpiUrlExtractor.UnescapeBusctlString(escaped));
    }

    [Fact]
    public void ParseQuotedStrings_DecodesEachBusctlString()
    {
        var values = AtSpiUrlExtractor.ParseQuotedStrings(
            @"a(so) 2 "":1.5"" ""/b\303\274cher"" "":1.6"" ""/x"""
        );

        Assert.Equal([":1.5", "/bücher", ":1.6", "/x"], values);
    }

    [Theory]
    [InlineData("chrome", @"b\303\274cher.test:18212/qa", "https://bücher.test:18212/qa")]
    [InlineData("chrome", "xn--bcher-kva.test/qa", "https://xn--bcher-kva.test/qa")]
    // KWin snapshot: process name and app ID both come from Chrome's window class.
    [InlineData("google-chrome", @"b\303\274cher.test:18212/qa", "https://bücher.test:18212/qa")]
    public void TryGetBrowserUrl_IdnAddressBar_MatchesPunycodeProfile(
        string processName,
        string busctlAddressText,
        string expectedUrl
    )
    {
        var extractor = new AtSpiUrlExtractor(BuildChromeTree(busctlAddressText));
        var profiles = new ProfileService(_profilesPath);
        profiles.AddProfile(
            new Profile
            {
                Id = Guid.NewGuid().ToString(),
                Name = "QA IDN",
                IsEnabled = true,
                UrlPatterns = ["xn--bcher-kva.test"],
            }
        );

        var url = extractor.TryGetBrowserUrl(processName, processName, "QA IDN - Google Chrome");

        Assert.Equal(expectedUrl, url);
        Assert.Equal("QA IDN", profiles.MatchProfile(processName, url).Profile?.Name);
    }

    [Fact]
    public void TryGetBrowserUrl_CollectionMatch_FindsTheAddressBarWithoutWalkingTheWindow()
    {
        var runner = BuildChromeTree(@"b\303\274cher.test:18212/qa");
        Respond(runner, FramePath, "GetMatches", $@"a(so) 1 "":1.5"" ""{EntryPath}""");
        var extractor = new AtSpiUrlExtractor(runner);

        var url = extractor.TryGetBrowserUrl("google-chrome", "google-chrome", "QA IDN - Google Chrome");

        Assert.Equal("https://bücher.test:18212/qa", url);
        var getMatches = runner.SupervisorInvocations
            .Where(invocation => invocation.Command.Arguments.Contains("GetMatches"))
            .Select(invocation => invocation.Command.Arguments.Skip(7))
            .ToList();
        // Showing+visible states (25, 30); combo box (11), edit bar and entry (77, 79) for the
        // address bar, then document-web (95, sign bit of word 2) for the page documents.
        Assert.Equal(
            [
                [
                    "(aiia{ss}iaiiasib)uib",
                    "2", "1107296256", "0", "1",
                    "0", "1",
                    "4", "2048", "0", "40960", "0", "2",
                    "0", "1", "false",
                    "1", "0", "true",
                ],
                [
                    "(aiia{ss}iaiiasib)uib",
                    "2", "1107296256", "0", "1",
                    "0", "1",
                    "4", "0", "0", DocumentRoleWord, "0", "2",
                    "0", "1", "false",
                    "1", "0", "true",
                ],
            ],
            getMatches
        );
        Assert.DoesNotContain(
            runner.SupervisorInvocations,
            invocation => invocation.Command.Arguments.Contains(FramePath)
                          && invocation.Command.Arguments.Contains("GetChildren")
        );
    }

    [Fact]
    public void TryGetBrowserUrl_CollectionMatch_IgnoresUrlLikeWebPageFields()
    {
        const string pageFieldPath = "/org/a11y/atspi/accessible/page-field";
        var runner = BuildChromeTree(@"b\303\274cher.test:18212/qa");
        Respond(
            runner,
            FramePath,
            "GetMatches",
            $@"a(so) 2 "":1.5"" ""{pageFieldPath}"" "":1.5"" ""{EntryPath}"""
        );
        RespondDocuments(runner, FramePath, DocumentPath);
        Respond(runner, DocumentPath, "GetMatches", $@"a(so) 1 "":1.5"" ""{pageFieldPath}""");
        Respond(runner, pageFieldPath, "GetRole", "u 79");
        Respond(runner, pageFieldPath, "GetState", $"au 2 {(1u << 25) | (1u << 30) | (1u << 11) | (1u << 18)} 0");
        Respond(
            runner,
            pageFieldPath,
            "GetInterfaces",
            @"as 2 ""org.a11y.atspi.Accessible"" ""org.a11y.atspi.Text"""
        );
        Respond(runner, pageFieldPath, "CharacterCount", "i 19");
        Respond(runner, pageFieldPath, "GetText", @"s ""example.com/landing""");
        var extractor = new AtSpiUrlExtractor(runner);

        var url = extractor.TryGetBrowserUrl("google-chrome", "google-chrome", "QA IDN - Google Chrome");

        Assert.Equal("https://bücher.test:18212/qa", url);
        Assert.DoesNotContain(
            runner.SupervisorInvocations,
            invocation => invocation.Command.Arguments.Contains(pageFieldPath)
        );
    }

    [Fact]
    public void TryGetBrowserUrl_CollectionMatch_FindsFirefoxComboBoxBelowHigherScoringPageFields()
    {
        // Firefox's URL bar is an unfocused combo box (score ~180) and carries a "tag"
        // attribute like page fields; a focused URL-like page entry would outscore it.
        const string urlBarPath = "/org/a11y/atspi/accessible/urlbar";
        const string pageFieldPath = "/org/a11y/atspi/accessible/page-field";
        var runner = BuildChromeTree("unused.example/path", appName: "Firefox");
        Respond(
            runner,
            FramePath,
            "GetMatches",
            $@"a(so) 2 "":1.5"" ""{urlBarPath}"" "":1.5"" ""{pageFieldPath}"""
        );
        RespondDocuments(runner, FramePath, DocumentPath);
        Respond(runner, DocumentPath, "GetMatches", $@"a(so) 1 "":1.5"" ""{pageFieldPath}""");
        Respond(runner, urlBarPath, "GetRole", "u 11");
        Respond(runner, urlBarPath, "GetState", $"au 2 {(1u << 25) | (1u << 30) | (1u << 18)} 0");
        Respond(runner, urlBarPath, "Name", @"s ""Search with Google or enter address""");
        Respond(
            runner,
            urlBarPath,
            "GetInterfaces",
            @"as 2 ""org.a11y.atspi.Accessible"" ""org.a11y.atspi.Text"""
        );
        Respond(runner, urlBarPath, "CharacterCount", "i 28");
        Respond(runner, urlBarPath, "GetText", @"s ""http://b\303\274cher.test:18212/qa/""");
        var extractor = new AtSpiUrlExtractor(runner);

        var url = extractor.TryGetBrowserUrl("firefox", "QA IDN — Mozilla Firefox");

        Assert.StartsWith("http://bücher.test:18212/qa", url);
        Assert.DoesNotContain(
            runner.SupervisorInvocations,
            invocation => invocation.Command.Arguments.Contains(pageFieldPath)
                          && !invocation.Command.Arguments.Contains("GetMatches")
        );
    }

    [Fact]
    public void TryGetBrowserUrl_CollectionMatch_FindsAddressBarAfter32PageFields()
    {
        var runner = BuildChromeTree("actual.example/path");
        var pageFields = Enumerable.Range(0, 32)
            .Select(i => $"/org/a11y/atspi/accessible/page-{i}").ToArray();
        var pageMatches = string.Join(" ", pageFields.Select(path => $@""":1.5"" ""{path}"""));
        // Model the server applying the requested count before client-side page filtering.
        runner.RespondWith(
            (file, args) => file == "busctl" && args.Contains(FramePath) && args.Contains("GetMatches")
                && !args.Contains(DocumentRoleWord) && args[^2] == "0",
            $@"a(so) 33 {pageMatches} "":1.5"" ""{EntryPath}""");
        Respond(runner, FramePath, "GetMatches", $"a(so) 32 {pageMatches}");
        RespondDocuments(runner, FramePath, DocumentPath);
        Respond(runner, DocumentPath, "GetMatches", $"a(so) 32 {pageMatches}");
        var extractor = new AtSpiUrlExtractor(runner);

        Assert.Equal("https://actual.example/path",
            extractor.TryGetBrowserUrl("chrome", "QA - Google Chrome"));
        Assert.DoesNotContain(runner.SupervisorInvocations,
            invocation => invocation.Command.Arguments.Contains(FramePath)
                && invocation.Command.Arguments.Contains("GetChildren"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void TryGetBrowserUrl_UncheckedPageFields_FallBackToWalk(bool documentQueryFails)
    {
        const string uncheckedPath = "/org/a11y/atspi/accessible/unchecked";
        var runner = BuildChromeTree("actual.example/path");
        Respond(runner, FramePath, "GetMatches",
            $@"a(so) 2 "":1.5"" ""{uncheckedPath}"" "":1.5"" ""{EntryPath}""");
        if (documentQueryFails)
        {
            runner.FailWhen((file, args) => file == "busctl"
                && args.Contains(FramePath) && args.Contains(DocumentRoleWord));
        }
        else
        {
            RespondDocuments(runner, FramePath, DocumentPath);
            runner.FailWhen((file, args) => file == "busctl"
                && args.Contains(DocumentPath) && args.Contains("GetMatches"));
        }
        Respond(runner, uncheckedPath, "GetRole", "u 77");
        Respond(runner, uncheckedPath, "Name", @"s ""https://wrong.example/path""");
        var extractor = new AtSpiUrlExtractor(runner);

        Assert.Equal("https://actual.example/path",
            extractor.TryGetBrowserUrl("chrome", "QA - Google Chrome"));
        Assert.DoesNotContain(runner.SupervisorInvocations,
            invocation => invocation.Command.Arguments.Contains(uncheckedPath));
        Assert.Contains(runner.SupervisorInvocations,
            invocation => invocation.Command.Arguments.Contains(FramePath)
                && invocation.Command.Arguments.Contains("GetChildren"));
    }

    [Fact]
    public void TryGetBrowserUrl_Fallback_SkipsThePageDocument()
    {
        const string pageFieldPath = "/org/a11y/atspi/accessible/page-field";
        var runner = BuildChromeTree("actual.example/path", false, DocumentPath);
        Respond(runner, FramePath, "GetMatches", "a(so) 0");
        Respond(runner, DocumentPath, "GetState", $"au 2 {(1u << 25) | (1u << 30)} 0");
        Respond(runner, DocumentPath, "GetRole", "u 95");
        Respond(runner, DocumentPath, "GetChildren", $@"a(so) 1 "":1.5"" ""{pageFieldPath}""");
        Respond(runner, pageFieldPath, "GetState",
            $"au 2 {(1u << 25) | (1u << 30) | (1u << 11) | (1u << 18)} 0");
        Respond(runner, pageFieldPath, "GetRole", "u 79");
        Respond(runner, pageFieldPath, "Name", @"s ""https://wrong.example/path""");
        var extractor = new AtSpiUrlExtractor(runner);

        Assert.Equal("https://actual.example/path",
            extractor.TryGetBrowserUrl("chrome", "QA - Google Chrome"));
        Assert.DoesNotContain(runner.SupervisorInvocations,
            invocation => invocation.Command.Arguments.Contains(pageFieldPath));
        Assert.DoesNotContain(runner.SupervisorInvocations,
            invocation => invocation.Command.Arguments.Contains(DocumentPath)
                && invocation.Command.Arguments.Contains("GetChildren"));
    }

    [Fact]
    public void TryGetBrowserUrl_CollectionMatch_StopsAfterStrongAddressBarCandidate()
    {
        const string laterPath = "/org/a11y/atspi/accessible/later";
        var runner = BuildChromeTree("actual.example/path");
        Respond(runner, FramePath, "GetMatches",
            $@"a(so) 2 "":1.5"" ""{EntryPath}"" "":1.5"" ""{laterPath}""");
        var extractor = new AtSpiUrlExtractor(runner);

        Assert.Equal("https://actual.example/path",
            extractor.TryGetBrowserUrl("chrome", "QA - Google Chrome"));
        Assert.DoesNotContain(runner.SupervisorInvocations,
            invocation => invocation.Command.Arguments.Contains(laterPath));
    }

    [Fact]
    public void TryGetBrowserUrl_FocusedWindowWithoutActiveState_IsFoundByTitle()
    {
        // KWin reports the freshly launched XWayland Chrome as focused before Chrome marks
        // any of its frames ACTIVE; the URL must still come back on the first walk.
        var runner = BuildChromeTree(@"b\303\274cher.test:18212/qa", frameActive: false);
        Respond(runner, FramePath, "Name", @"s ""QA IDN - Google Chrome""");
        Respond(runner, FramePath, "GetMatches", $@"a(so) 1 "":1.5"" ""{EntryPath}""");
        var extractor = new AtSpiUrlExtractor(runner);

        Assert.Equal(
            "https://bücher.test:18212/qa",
            extractor.TryGetBrowserUrl("google-chrome", "google-chrome", "QA IDN - Google Chrome")
        );
    }

    [Fact]
    public void TryGetBrowserUrl_StaleActiveWindow_LosesToTheFocusedTitle()
    {
        const string staleFramePath = "/org/a11y/atspi/accessible/stale-frame";
        const string staleEntryPath = "/org/a11y/atspi/accessible/stale-entry";
        var runner = new FakeProcessRunner
        {
            Default = new ProcessRunResult(true, false, 0, "('unix:path=/tmp/a11y-test',)\n", ""),
        };
        // The window that lost focus still claims ACTIVE and comes first in the app.
        Respond(runner, AppPath, "GetChildren",
            $@"a(so) 2 "":1.5"" ""{staleFramePath}"" "":1.5"" ""{FramePath}""");
        Respond(runner, staleFramePath, "GetRole", "u 23");
        Respond(runner, staleFramePath, "GetState", $"au 2 {1u << 1} 0");
        Respond(runner, staleFramePath, "Name", @"s ""Other page - Google Chrome""");
        Respond(runner, staleFramePath, "GetChildren", "a(so) 0");
        Respond(runner, staleFramePath, "GetMatches",
            $@"a(so) 1 "":1.5"" ""{staleEntryPath}""");
        Respond(runner, staleEntryPath, "GetRole", "u 79");
        Respond(runner, staleEntryPath, "GetState", $"au 2 {(1u << 25) | (1u << 30)} 0");
        Respond(runner, staleEntryPath, "Name", @"s ""Address and search bar""");
        Respond(runner, staleEntryPath, "GetInterfaces",
            @"as 2 ""org.a11y.atspi.Accessible"" ""org.a11y.atspi.Text""");
        Respond(runner, staleEntryPath, "CharacterCount", "i 21");
        Respond(runner, staleEntryPath, "GetText", @"s ""wrong.example/other""");
        Respond(runner, FramePath, "Name", @"s ""QA IDN - Google Chrome""");
        Respond(runner, FramePath, "GetMatches", $@"a(so) 1 "":1.5"" ""{EntryPath}""");
        AddChromeTree(runner, "actual.example/path", frameActive: false);
        var extractor = new AtSpiUrlExtractor(runner);

        Assert.Equal(
            "https://actual.example/path",
            extractor.TryGetBrowserUrl("google-chrome", "google-chrome", "QA IDN - Google Chrome")
        );
    }

    // Registry root -> Chrome app -> active frame -> focused, editable address bar.
    private static FakeProcessRunner BuildChromeTree(
        string addressText, bool addressFocused = true, string? leadingChildPath = null,
        bool frameActive = true, string appName = "Google Chrome"
    )
    {
        var runner = new FakeProcessRunner
        {
            Default = new ProcessRunResult(true, false, 0, "('unix:path=/tmp/a11y-test',)\n", ""),
        };
        AddChromeTree(runner, addressText, addressFocused, leadingChildPath, frameActive, appName);
        return runner;
    }

    private static void AddChromeTree(
        FakeProcessRunner runner, string addressText, bool addressFocused = true,
        string? leadingChildPath = null, bool frameActive = true, string appName = "Google Chrome"
    )
    {
        const uint showingVisibleFocusedEditable =
            (1u << 25) | (1u << 30) | (1u << 11) | (1u << 18);

        Respond(runner, "/org/a11y/atspi/accessible/root", "GetChildren", $@"a(so) 1 "":1.5"" ""{AppPath}""");
        Respond(runner, AppPath, "Name", $@"s ""{appName}""");
        Respond(runner, AppPath, "GetChildren", $@"a(so) 1 "":1.5"" ""{FramePath}""");
        Respond(runner, FramePath, "GetRole", "u 23");
        Respond(runner, FramePath, "GetState", $"au 2 {(frameActive ? 1u << 1 : 0u)} 0");
        Respond(runner, FramePath, "GetChildren", leadingChildPath is null
            ? $@"a(so) 1 "":1.5"" ""{EntryPath}"""
            : $@"a(so) 2 "":1.5"" ""{leadingChildPath}"" "":1.5"" ""{EntryPath}""");
        Respond(runner, EntryPath, "GetRole", "u 79");
        Respond(runner, EntryPath, "GetState", $"au 2 {showingVisibleFocusedEditable & (addressFocused ? uint.MaxValue : ~(1u << 11))} 0");
        Respond(
            runner,
            EntryPath,
            "GetInterfaces",
            @"as 2 ""org.a11y.atspi.Accessible"" ""org.a11y.atspi.Text"""
        );
        Respond(runner, EntryPath, "CharacterCount", "i 20");
        Respond(runner, EntryPath, "GetText", $@"s ""{addressText}""");
        Respond(runner, EntryPath, "GetChildren", "a(so) 0");
    }

    // GetMatches responses answer the URL-field query only; see RespondDocuments.
    private static void Respond(FakeProcessRunner runner, string path, string member, string stdout)
    {
        runner.RespondWith(
            (file, args) => file == "busctl" && args.Contains(path) && args.Contains(member)
                && !args.Contains(DocumentRoleWord),
            stdout + "\n"
        );
    }

    // Answers the document-web Collection query on path with the given page documents.
    private static void RespondDocuments(FakeProcessRunner runner, string path, params string[] documents)
    {
        runner.RespondWith(
            (file, args) => file == "busctl" && args.Contains(path) && args.Contains(DocumentRoleWord),
            $"a(so) {documents.Length} {string.Join(" ", documents.Select(document => $@""":1.5"" ""{document}"""))}\n"
        );
    }
}
