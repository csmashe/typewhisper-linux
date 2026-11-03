using TypeWhisper.Linux.Services.ActiveWindow;
using Xunit;

namespace TypeWhisper.Linux.Tests;

/// <summary>
///     Pure-function tests for the gvariant-tuple unwrap and JSON-payload
///     parsing in <see cref="GnomeWindowCallsProvider" />. The provider talks
///     to a third-party GNOME Shell extension whose output formatting is
///     stable but easy to get wrong on this end — these tests pin the
///     parsing to a handful of representative gdbus outputs.
/// </summary>
public sealed class GnomeWindowCallsProviderTests
{
    [Fact]
    public void UnwrapsSimpleGvariantTuple()
    {
        const string output = "('[]',)";
        Assert.Equal("[]", GnomeWindowCallsProvider.UnwrapGvariantString(output));
    }

    [Fact]
    public void UnescapesEmbeddedSingleQuotes()
    {
        // Window title with apostrophe: gdbus prints \'  inside the
        // gvariant single-quoted string.
        const string output = """('[{"title":"can\'t open"}]',)""";
        var inner = GnomeWindowCallsProvider.UnwrapGvariantString(output);
        Assert.Equal("""[{"title":"can't open"}]""", inner);
    }

    [Fact]
    public void UnescapesBackslashPairs()
    {
        // JSON inside contains a literal newline escape "\n" (two chars).
        // gvariant escapes the backslash so we see \\n on the wire and
        // need to emit \n for the JSON parser to consume.
        const string output = """('[{"title":"line1\\nline2"}]',)""";
        var inner = GnomeWindowCallsProvider.UnwrapGvariantString(output);
        Assert.Equal("""[{"title":"line1\nline2"}]""", inner);
    }

    [Fact]
    public void ReturnsNullForMalformedOutput()
    {
        Assert.Null(GnomeWindowCallsProvider.UnwrapGvariantString(""));
        Assert.Null(GnomeWindowCallsProvider.UnwrapGvariantString("not a tuple"));
        Assert.Null(GnomeWindowCallsProvider.UnwrapGvariantString("(no-quotes,)"));
        Assert.Null(GnomeWindowCallsProvider.UnwrapGvariantString("('mismatched\",)"));
        Assert.Null(GnomeWindowCallsProvider.UnwrapGvariantString("""('bad \u12',)"""));
        Assert.Null(GnomeWindowCallsProvider.UnwrapGvariantString("""('bad \Uffffffff',)"""));
    }

    [Fact]
    public void ParsesDoubleQuotedOutputWhenABackgroundTitleHasAnApostrophe()
    {
        // GLib switches to double quotes once any title contains an
        // apostrophe. Captured from GLib.Variant('(s)', ...).print_(True).
        const string output =
            """("[{\"id\": 1, \"title\": \"QA background owner's notes\", \"has_focus\": false}, {\"id\": 2, \"title\": \"QA focused field\", \"wm_class\": \"x\", \"pid\": 1, \"has_focus\": true}]",)""";

        var focused = GnomeWindowCallsProvider.ParseFocusedWindow(output);

        Assert.NotNull(focused);
        Assert.Equal("2", focused.Value.WindowId);
        Assert.Equal("QA focused field", focused.Value.Title);
    }

    [Fact]
    public void UnescapesGlibControlAndUnicodeEscapes()
    {
        // Captured from GLib: a title with both quote kinds, a JSON tab
        // escape, and U+200E, which GLib prints as ‎.
        const string output =
            """("[{\"id\": 1, \"title\": \"it's \\\"quoted\\\"\", \"has_focus\": false}, {\"id\": 2, \"title\": \"tab\\there ‎ mark\", \"wm_class\": \"x\", \"pid\": 1, \"has_focus\": true}]",)""";

        var focused = GnomeWindowCallsProvider.ParseFocusedWindow(output);

        Assert.NotNull(focused);
        Assert.Equal("tab\there ‎ mark", focused.Value.Title);
        Assert.Equal("\U0001F600\n", GnomeWindowCallsProvider.UnwrapGvariantString("""('\U0001f600\n',)"""));
    }

    [Fact]
    public void ParsesFocusedWindowFromRealWorldListOutput()
    {
        // Verbatim shape from `gdbus call ... .List` on a system with
        // the Window Calls extension installed (Fedora 44, GNOME 46) —
        // canonical field is "has_focus", title and wm_class are inline.
        const string output =
            """('[{"id":111,"title":"TypeWhisper","wm_class":"typewhisper","wm_class_instance":"typewhisper","pid":15798,"has_focus":false},{"id":222,"title":"Mozilla Firefox","wm_class":"org.mozilla.firefox","wm_class_instance":"org.mozilla.firefox","pid":31434,"has_focus":true}]',)""";

        var focused = GnomeWindowCallsProvider.ParseFocusedWindow(output);

        Assert.NotNull(focused);
        Assert.Equal("org.mozilla.firefox", focused.Value.WmClass);
        Assert.Equal(31434, focused.Value.Pid);
        Assert.Equal("222", focused.Value.WindowId);
        Assert.Equal("Mozilla Firefox", focused.Value.Title);
    }

    [Fact]
    public void AcceptsLegacyFocusFieldFromForkedExtensions()
    {
        // The original ickyicky fork used `focus` instead of `has_focus`.
        // We accept either so users with the older variant aren't stuck.
        const string output = """('[{"wm_class":"x","pid":1,"id":2,"focus":true}]',)""";

        var focused = GnomeWindowCallsProvider.ParseFocusedWindow(output);

        Assert.NotNull(focused);
        Assert.Equal("x", focused.Value.WmClass);
    }

    [Fact]
    public void FallsBackToWmClassInstanceWhenWmClassMissing()
    {
        const string output =
            """('[{"wm_class_instance":"Code","pid":99,"id":1,"has_focus":true}]',)""";

        var focused = GnomeWindowCallsProvider.ParseFocusedWindow(output);

        Assert.NotNull(focused);
        Assert.Equal("Code", focused.Value.WmClass);
    }

    [Fact]
    public void ReturnsNullWhenNoWindowHasFocus()
    {
        // Transient state during workspace switches — no focused window.
        const string output = """('[{"wm_class":"firefox","pid":1,"id":2,"has_focus":false}]',)""";

        Assert.Null(GnomeWindowCallsProvider.ParseFocusedWindow(output));
    }

    [Fact]
    public void ReturnsNullForEmptyArray()
    {
        Assert.Null(GnomeWindowCallsProvider.ParseFocusedWindow("('[]',)"));
    }

    [Fact]
    public void HandlesStringWindowIdVariant()
    {
        const string output = """('[{"wm_class":"x","pid":1,"id":"abc-123","has_focus":true}]',)""";

        var focused = GnomeWindowCallsProvider.ParseFocusedWindow(output);

        Assert.NotNull(focused);
        Assert.Equal("abc-123", focused.Value.WindowId);
    }
}