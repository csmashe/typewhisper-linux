using System.Text;

namespace TypeWhisper.PluginSDK.Helpers;

/// <summary>Removes <c>&lt;think&gt;…&lt;/think&gt;</c> reasoning blocks from chat completion text.</summary>
public static class ThinkingBlockFilter
{
    public static string Strip(string text)
    {
        var start = text.IndexOf("<think>", StringComparison.OrdinalIgnoreCase);
        if (start < 0)
            return text;

        var visible = new StringBuilder(text.Length);
        var position = 0;
        while (start >= 0)
        {
            visible.Append(text.AsSpan(position, start - position));
            var end = text.IndexOf("</think>", start + 7, StringComparison.OrdinalIgnoreCase);
            if (end < 0)
                return visible.ToString().Trim();
            position = end + 8;
            start = text.IndexOf("<think>", position, StringComparison.OrdinalIgnoreCase);
        }

        visible.Append(text.AsSpan(position));
        return visible.ToString().TrimStart();
    }
}

/// <summary>Streaming counterpart of <see cref="ThinkingBlockFilter" />; tags may be split across deltas.</summary>
public sealed class ThinkingBlockStreamFilter
{
    private string _pending = "";
    private bool _inside;
    private bool _hasVisibleText;
    private bool _trimLeadingWhitespace = true;

    public bool SawThinkBlock { get; private set; }

    public IEnumerable<string> Push(string delta)
    {
        var text = _pending + delta;
        _pending = "";
        var visible = new StringBuilder();
        var position = 0;
        while (position < text.Length)
        {
            var tag = _inside ? "</think>" : "<think>";
            var remaining = text.AsSpan(position);
            if (remaining.StartsWith(tag, StringComparison.OrdinalIgnoreCase))
            {
                _inside = !_inside;
                if (_inside)
                    SawThinkBlock = true;
                else if (!_hasVisibleText)
                    _trimLeadingWhitespace = true;
                position += tag.Length;
                continue;
            }

            // SSE boundaries can split tags anywhere. Hold a possible tag prefix until
            // the next delta disambiguates it, so reasoning never leaks into output.
            if ((remaining.Length < 7 && "<think>".AsSpan().StartsWith(remaining, StringComparison.OrdinalIgnoreCase))
                || (remaining.Length < 8 && "</think>".AsSpan().StartsWith(remaining, StringComparison.OrdinalIgnoreCase)))
            {
                _pending = text[position..];
                break;
            }

            var character = text[position++];
            if (_inside || (_trimLeadingWhitespace && char.IsWhiteSpace(character)))
                continue;
            _trimLeadingWhitespace = false;
            _hasVisibleText = true;
            visible.Append(character);
        }

        return visible.Length > 0 ? new[] { visible.ToString() } : [];
    }

    public string Flush()
    {
        var remaining = _inside ? "" : _pending;
        _pending = "";
        if (_trimLeadingWhitespace)
            remaining = remaining.TrimStart();
        if (remaining.Length == 0)
            return remaining;

        _hasVisibleText = true;
        _trimLeadingWhitespace = false;
        return remaining;
    }
}
