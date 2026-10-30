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
        var getMatches = Assert.Single(
            runner.SupervisorInvocations,
            invocation => invocation.Command.Arguments.Contains("GetMatches")
        );
        // Showing+visible states (25, 30) and the editbar/entry role word (77, 79).
        Assert.Equal(
            [
                "(aiia{ss}iaiiasib)uib",
                "2", "1107296256", "0", "1",
                "0", "1",
                "4", "0", "0", "40960", "0", "2",
                "0", "1", "false",
                "1", "0", "true",
            ],
            getMatches.Command.Arguments.Skip(6)
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
        Respond(runner, pageFieldPath, "GetAttributes", @"a{ss} 2 ""tag"" ""input"" ""id"" ""w""");
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
                          && invocation.Command.Arguments.Contains("GetText")
        );
    }

    [Fact]
    public void TryGetBrowserUrl_CollectionMatch_FindsAddressBarAfter32PageFields()
    {
        var runner = BuildChromeTree("actual.example/path");
        var pageFields = Enumerable.Range(0, 32)
            .Select(i => $"/org/a11y/atspi/accessible/page-{i}").ToArray();
        var pageMatches = string.Join(" ", pageFields.Select(path => $@""":1.5"" ""{path}"""));
        // Model the server applying the requested count before client-side tag filtering.
        runner.RespondWith(
            (file, args) => file == "busctl" && args.Contains("GetMatches")
                && args[^2] == "0",
            $@"a(so) 33 {pageMatches} "":1.5"" ""{EntryPath}""");
        Respond(runner, FramePath, "GetMatches", $"a(so) 32 {pageMatches}");
        foreach (var path in pageFields)
        {
            Respond(runner, path, "GetAttributes", @"a{ss} 1 ""tag"" ""input""");
        }
        var extractor = new AtSpiUrlExtractor(runner);

        Assert.Equal("https://actual.example/path",
            extractor.TryGetBrowserUrl("chrome", "QA - Google Chrome"));
        Assert.DoesNotContain(runner.SupervisorInvocations,
            invocation => invocation.Command.Arguments.Contains(FramePath)
                && invocation.Command.Arguments.Contains("GetChildren"));
    }

    [Fact]
    public void TryGetBrowserUrl_AttributeReadFailure_FallbackIgnoresFocusedPageField()
    {
        const string uncheckedPath = "/org/a11y/atspi/accessible/unchecked";
        const string pageFieldPath = "/org/a11y/atspi/accessible/page-field";
        var runner = BuildChromeTree("actual.example/path", false, pageFieldPath);
        Respond(runner, FramePath, "GetMatches",
            $@"a(so) 1 "":1.5"" ""{uncheckedPath}""");
        runner.FailWhen((file, args) => file == "busctl"
            && args.Contains(uncheckedPath) && args.Contains("GetAttributes"));
        Respond(runner, pageFieldPath, "GetAttributes", @"a{ss} 1 ""tag"" ""input""");
        Respond(runner, pageFieldPath, "GetState",
            $"au 2 {(1u << 25) | (1u << 30) | (1u << 11) | (1u << 18)} 0");
        Respond(runner, pageFieldPath, "GetRole", "u 79");
        Respond(runner, pageFieldPath, "Name", @"s ""https://wrong.example/path""");
        Respond(runner, pageFieldPath, "GetInterfaces", "as 0");
        Respond(runner, pageFieldPath, "GetChildren", "a(so) 0");
        var extractor = new AtSpiUrlExtractor(runner);

        Assert.Equal("https://actual.example/path",
            extractor.TryGetBrowserUrl("chrome", "QA - Google Chrome"));
        Assert.Contains(runner.SupervisorInvocations,
            invocation => invocation.Command.Arguments.Contains(uncheckedPath)
                && invocation.Command.Arguments.Contains("GetAttributes"));
        Assert.Contains(runner.SupervisorInvocations,
            invocation => invocation.Command.Arguments.Contains(FramePath)
                && invocation.Command.Arguments.Contains("GetChildren"));
    }

    [Fact]
    public void TryGetBrowserUrl_Fallback_ReadsAttributesOnlyForUrlCandidates()
    {
        const string buttonPath = "/org/a11y/atspi/accessible/reload-button";
        var runner = BuildChromeTree("actual.example/path", pageFieldPath: buttonPath);
        Respond(runner, FramePath, "GetMatches", "a(so) 0");
        Respond(runner, buttonPath, "GetState", $"au 2 {(1u << 25) | (1u << 30)} 0");
        Respond(runner, buttonPath, "GetRole", "u 42");
        Respond(runner, buttonPath, "Name", @"s ""Reload""");
        Respond(runner, buttonPath, "GetInterfaces", "as 0");
        Respond(runner, buttonPath, "GetChildren", "a(so) 0");
        var extractor = new AtSpiUrlExtractor(runner);

        Assert.Equal("https://actual.example/path",
            extractor.TryGetBrowserUrl("chrome", "QA - Google Chrome"));
        Assert.DoesNotContain(runner.SupervisorInvocations,
            invocation => invocation.Command.Arguments.Contains(buttonPath)
                && invocation.Command.Arguments.Contains("GetAttributes"));
        Assert.Contains(runner.SupervisorInvocations,
            invocation => invocation.Command.Arguments.Contains(EntryPath)
                && invocation.Command.Arguments.Contains("GetAttributes"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void TryGetBrowserUrl_CollectionMatch_SkipsUncheckedAttributes(bool requestFails)
    {
        const string uncheckedPath = "/org/a11y/atspi/accessible/unchecked";
        var runner = BuildChromeTree("actual.example/path");
        Respond(runner, FramePath, "GetMatches",
            $@"a(so) 2 "":1.5"" ""{uncheckedPath}"" "":1.5"" ""{EntryPath}""");
        if (requestFails)
        {
            runner.FailWhen((file, args) => file == "busctl"
                && args.Contains(uncheckedPath) && args.Contains("GetAttributes"));
        }
        else
        {
            Respond(runner, uncheckedPath, "GetAttributes", "");
        }
        Respond(runner, uncheckedPath, "GetRole", "u 77");
        Respond(runner, uncheckedPath, "Name", @"s ""https://wrong.example/path""");
        var extractor = new AtSpiUrlExtractor(runner);

        Assert.Equal("https://actual.example/path",
            extractor.TryGetBrowserUrl("chrome", "QA - Google Chrome"));
        Assert.Single(runner.SupervisorInvocations,
            invocation => invocation.Command.Arguments.Contains(uncheckedPath));
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

    // Registry root -> Chrome app -> active frame -> focused, editable address bar.
    private static FakeProcessRunner BuildChromeTree(
        string addressText, bool addressFocused = true, string? pageFieldPath = null
    )
    {
        const uint showingVisibleFocusedEditable =
            (1u << 25) | (1u << 30) | (1u << 11) | (1u << 18);
        var runner = new FakeProcessRunner
        {
            Default = new ProcessRunResult(true, false, 0, "('unix:path=/tmp/a11y-test',)\n", ""),
        };

        Respond(runner, "/org/a11y/atspi/accessible/root", "GetChildren", $@"a(so) 1 "":1.5"" ""{AppPath}""");
        Respond(runner, AppPath, "Name", @"s ""Google Chrome""");
        Respond(runner, AppPath, "GetChildren", $@"a(so) 1 "":1.5"" ""{FramePath}""");
        Respond(runner, FramePath, "GetRole", "u 23");
        Respond(runner, FramePath, "GetState", $"au 2 {1u << 1} 0");
        Respond(runner, FramePath, "GetChildren", pageFieldPath is null
            ? $@"a(so) 1 "":1.5"" ""{EntryPath}"""
            : $@"a(so) 2 "":1.5"" ""{pageFieldPath}"" "":1.5"" ""{EntryPath}""");
        Respond(runner, EntryPath, "GetAttributes", "a{ss} 0");
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
        return runner;
    }

    private static void Respond(FakeProcessRunner runner, string path, string member, string stdout)
    {
        runner.RespondWith(
            (file, args) => file == "busctl" && args.Contains(path) && args.Contains(member),
            stdout + "\n"
        );
    }
}
