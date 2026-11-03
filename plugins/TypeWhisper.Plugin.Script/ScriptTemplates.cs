namespace TypeWhisper.Plugin.Script;

/// <summary>A built-in bash command offered as a disabled starting point.</summary>
/// <param name="Id">Stable identifier; also the localization key segment.</param>
/// <param name="Command">bash source that reads stdin and writes the replacement to stdout.</param>
internal sealed record ScriptTemplate(string Id, string Command);

/// <summary>
///     Built-in templates. Each only transforms stdin into stdout with bash builtins, so none
///     touches files or the network, and each writes no trailing newline the input did not have.
/// </summary>
internal static class ScriptTemplates
{
    public const string Shell = "bash";

    // UTF-8 locale so case changes cover letters such as ä whatever the session locale.
    private const string Utf8Locale = "LC_ALL=C.UTF-8\n";

    // Reads all of stdin, including trailing newlines, without the stripping $(cat) does.
    private const string ReadInput = "IFS= read -r -d '' text\n";

    // Splits the input into lines with any carriage returns removed.
    private const string ReadLines =
        "shopt -s extglob\n"
        + ReadInput
        + "text=${text//$'\\r'/}\n"
        + "mapfile -t lines <<< \"${text%$'\\n'}\"\n";

    public static readonly IReadOnlyList<ScriptTemplate> All =
    [
        new("uppercase", Utf8Locale + ReadInput + "printf '%s' \"${text^^}\"\n"),
        new("lowercase", Utf8Locale + ReadInput + "printf '%s' \"${text,,}\"\n"),
        new(
            "trim",
            Utf8Locale
                + "shopt -s extglob\n"
                + ReadInput
                + "text=${text##+([[:space:]])}\n"
                + "text=${text%%+([[:space:]])}\n"
                + "printf '%s' \"$text\"\n"
        ),
        new(
            "clean-spacing",
            Utf8Locale
                + ReadLines
                + """
                out=
                for line in "${lines[@]}"; do
                  line=${line//+([[:blank:]])/ }
                  line=${line# }
                  line=${line% }
                  out+=$line$'\n'
                done
                out=${out##+([[:space:]])}
                out=${out%%+([[:space:]])}
                printf '%s' "$out"

                """
        ),
        new(
            "bullets",
            Utf8Locale
                + ReadLines
                + """
                bullet='^[-*+][[:space:]]+'
                out=
                for line in "${lines[@]}"; do
                  line=${line##+([[:space:]])}
                  line=${line%%+([[:space:]])}
                  [[ -z $line ]] && continue
                  [[ $line =~ $bullet ]] || line="- $line"
                  out+=${out:+$'\n'}$line
                done
                printf '%s' "$out"

                """
        ),
        new(
            "checklist",
            Utf8Locale
                + ReadLines
                + """
                task='^[-*+][[:space:]]+\[[ xX]\]'
                bullet='^[-*+][[:space:]]+(.*)$'
                out=
                for line in "${lines[@]}"; do
                  line=${line##+([[:space:]])}
                  line=${line%%+([[:space:]])}
                  [[ -z $line ]] && continue
                  if [[ ! $line =~ $task ]]; then
                    [[ $line =~ $bullet ]] && line=${BASH_REMATCH[1]}
                    line="- [ ] $line"
                  fi
                  out+=${out:+$'\n'}$line
                done
                printf '%s' "$out"

                """
        ),
        new(
            "quote",
            Utf8Locale
                + ReadLines
                + """
                out=
                for line in "${lines[@]}"; do
                  [[ $line == '>'* ]] || line="> $line"
                  out+=${out:+$'\n'}$line
                done
                printf '%s' "$out"

                """
        ),
        new(
            "json",
            Utf8Locale
                + ReadInput
                + """
                text=${text//\\/\\\\}
                text=${text//\"/\\\"}
                text=${text//$'\n'/\\n}
                text=${text//$'\r'/\\r}
                text=${text//$'\t'/\\t}
                text=${text//$'\b'/\\b}
                text=${text//$'\f'/\\f}
                control='^([^[:cntrl:]]*)([[:cntrl:]])(.*)$'
                out=
                while [[ $text =~ $control ]]; do
                  printf -v code '\\u%04x' "'${BASH_REMATCH[2]}"
                  out+=${BASH_REMATCH[1]}$code
                  text=${BASH_REMATCH[3]}
                done
                printf '"%s"' "$out$text"

                """
        ),
        new(
            "url",
            // Byte-wise, so every UTF-8 byte outside the unreserved set is percent-encoded.
            "LC_ALL=C\n"
                + ReadInput
                + """
                out=
                for ((i = 0; i < ${#text}; i++)); do
                  c=${text:i:1}
                  case $c in
                    [A-Za-z0-9._~-]) out+=$c ;;
                    *) printf -v hex '%%%02X' "'$c"; out+=$hex ;;
                  esac
                done
                printf '%s' "$out"

                """
        ),
    ];
}
