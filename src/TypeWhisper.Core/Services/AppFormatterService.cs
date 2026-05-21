using System.Net;
using System.Text;

namespace TypeWhisper.Core.Services;

/// <summary>
///     Formats transcribed text based on the target application.
///     Maps known processes to output formats (markdown, html, code, plaintext).
/// </summary>
public static class AppFormatterService
{
    private static readonly Dictionary<string, string> ProcessFormatMap = new(
        StringComparer.OrdinalIgnoreCase
    )
    {
        // Markdown apps
        ["Obsidian"] = "markdown",
        ["Notion"] = "markdown",
        ["marktext"] = "markdown",
        ["Typora"] = "markdown",
        ["Bear"] = "markdown",

        // HTML apps (email clients)
        ["OUTLOOK"] = "html",
        ["Thunderbird"] = "html",

        // Code editors
        ["Code"] = "code", // VS Code
        ["devenv"] = "code", // Visual Studio
        ["rider64"] = "code", // JetBrains Rider
        ["idea64"] = "code", // IntelliJ
        ["WindowsTerminal"] = "code",
        ["cmd"] = "code",
        ["powershell"] = "code",
        ["pwsh"] = "code",
        ["cursor"] = "code"
    };

    /// <summary>
    ///     Formats text for the given target application process.
    ///     Returns the original text if no format mapping exists.
    /// </summary>
    public static string Format(string text, string? processName)
    {
        if (string.IsNullOrEmpty(processName))
        {
            return text;
        }

        var format = ResolveFormat(processName);
        return format switch
        {
            "markdown" => FormatAsMarkdown(text),
            "html" => FormatAsHtml(text),
            _ => text // code + plaintext = passthrough
        };
    }

    private static string ResolveFormat(string processName)
    {
        if (ProcessFormatMap.TryGetValue(processName, out var format))
        {
            return format;
        }

        // Partial match for process names that include version suffixes
        foreach (var (key, value) in ProcessFormatMap)
        {
            if (processName.Contains(key, StringComparison.OrdinalIgnoreCase))
            {
                return value;
            }
        }

        return "plaintext";
    }

    private static string FormatAsMarkdown(string text)
    {
        var lines = text.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var trimmed = lines[i].TrimStart();
            if (trimmed.StartsWith("bullet ", StringComparison.OrdinalIgnoreCase))
            {
                lines[i] = "- " + trimmed[7..];
            }
            else if (trimmed.StartsWith("* ") || trimmed.StartsWith("- "))
            {
            }
        }

        return string.Join('\n', lines);
    }

    private static string FormatAsHtml(string text)
    {
        var lines = text.Split('\n');
        var result = new StringBuilder();
        var inList = false;

        foreach (var line in lines)
        {
            var trimmed = line.TrimStart();
            var bullet = ExtractBulletContent(trimmed);

            if (bullet is not null)
            {
                if (!inList)
                {
                    result.AppendLine("<ul>");
                    inList = true;
                }

                result.AppendLine($"  <li>{WebUtility.HtmlEncode(bullet)}</li>");
            }
            else
            {
                if (inList)
                {
                    result.AppendLine("</ul>");
                    inList = false;
                }

                if (!string.IsNullOrWhiteSpace(trimmed))
                {
                    result.AppendLine($"<p>{WebUtility.HtmlEncode(trimmed)}</p>");
                }
            }
        }

        if (inList)
        {
            result.AppendLine("</ul>");
        }

        return result.ToString().TrimEnd();
    }

    private static string? ExtractBulletContent(string line)
    {
        if (line.StartsWith("- "))
        {
            return line[2..];
        }

        if (line.StartsWith("* "))
        {
            return line[2..];
        }

        if (line.StartsWith("bullet ", StringComparison.OrdinalIgnoreCase))
        {
            return line[7..];
        }

        return null;
    }
}