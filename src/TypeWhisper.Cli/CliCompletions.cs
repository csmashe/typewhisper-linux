using System.Text;
using System.Text.Json;

namespace TypeWhisper.Cli;

/// <summary>
///     Generates the bash, zsh and fish completion scripts for <c>typewhisper-cli</c> and parses the
///     IDs its hidden <c>__complete</c> helper offers. One command table feeds every shell so their
///     contexts stay aligned; it mirrors the grammar <see cref="Models.CliOptions.Parse" /> accepts.
/// </summary>
internal static class CliCompletions
{
    private const string CommandName = "typewhisper-cli";

    // IDs come from plugins. Shells read one per line and insert it unquoted, so an ID with a
    // line break, whitespace or shell syntax is never offered.
    private const string UnsafeIdCharacters = "$`;|&<>(){}[]'\"\\*?!#~";

    // Declared before the tables below: static initializers run in textual order.
    /// <summary>Shells accepted by <c>typewhisper-cli completions &lt;shell&gt;</c>.</summary>
    public static IReadOnlyList<string> Shells { get; } = ["bash", "zsh", "fish"];

    private static readonly string[] s_commands =
        ["status", "models", "history", "last", "dictation", "transcribe", "completions"];

    private static readonly string[] s_globalOptions = ["--token", "--api-token", "--json", "--help", "--version"];

    // An argument following one of these options is its value, never a command word.
    private static readonly string[] s_valueOptions =
    [
        "--token", "--api-token", "--language", "--language-hint", "--task", "--translate-to",
        "--response-format", "--prompt", "--engine", "--model", "--query", "--limit", "--offset",
    ];

    // Commands whose first operand selects a subcommand, so their options depend on it.
    private static readonly string[] s_subcommandCommands = ["models", "history", "dictation"];

    // Command options keyed by "command" or "command subcommand", as CliOptions.Parse allows them.
    private static readonly (string Context, string[] Options)[] s_commandOptions =
    [
        ("transcribe", [
            "--language", "--language-hint", "--task", "--translate-to", "--response-format", "--prompt",
            "--engine", "--model", "--no-corrections", "--await-download",
        ]),
        ("models load", ["--engine", "--model"]),
        ("models delete", ["--engine", "--model"]),
        ("models unload", ["--engine"]),
        ("history", ["--query", "--limit", "--offset"]),
        ("history search", ["--limit", "--offset"]),
    ];

    // Operands keyed by "command/position"; the first entry is the kind (words or files).
    private static readonly (string Context, string[] Entry)[] s_positionals =
    [
        ("/0", ["words", .. s_commands]),
        ("models/1", ["words", "list", "load", "unload", "delete"]),
        ("history/1", ["words", "search", "last"]),
        ("dictation/1", ["words", "start", "stop", "status", "result"]),
        ("completions/1", ["words", .. Shells]),
        ("transcribe/1", ["files"]),
    ];

    private static readonly (string Option, string[] Values)[] s_optionValues =
    [
        ("--task", ["transcribe", "translate"]),
        ("--response-format", ["json", "verbose_json", "text", "srt", "vtt"]),
    ];

    /// <summary>Returns the completion script for <paramref name="shell" />, or null for an unsupported shell.</summary>
    public static string? Script(string shell)
    {
        return shell switch
        {
            "bash" => Header("bash", $"source <({CommandName} completions bash)") + ShellCandidates() + BashGlue,
            "zsh" => $"#compdef {CommandName}\n"
                + Header("zsh", $"source <({CommandName} completions zsh)")
                + ShellCandidates()
                + ZshGlue,
            "fish" => Header("fish", $"{CommandName} completions fish | source") + FishScript(),
            _ => null,
        };
    }

    /// <summary>
    ///     Returns the engine IDs, or the model IDs (optionally of one engine), from a
    ///     <c>GET /v1/models</c> response.
    /// </summary>
    public static IReadOnlyList<string> Ids(string modelsJson, bool engines, string? engine = null)
    {
        using var document = JsonDocument.Parse(modelsJson);
        if (document.RootElement.ValueKind != JsonValueKind.Object
            || !document.RootElement.TryGetProperty("models", out var models)
            || models.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var ids = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        // ReSharper disable once LoopCanBeConvertedToQuery -- the loop dedupes through `seen` and keeps first-seen order; a query would hide that side effect in a Where.
        foreach (var model in models.EnumerateArray().Where(model => model.ValueKind == JsonValueKind.Object))
        {
            var modelEngine = Text(model, "engine");
            if (!engines
                && !string.IsNullOrWhiteSpace(engine)
                && !string.Equals(modelEngine, engine.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var id = engines ? modelEngine : Text(model, "id");
            if (!string.IsNullOrWhiteSpace(id) && !id.Any(IsUnsafeIdCharacter) && seen.Add(id))
            {
                ids.Add(id);
            }
        }

        return ids;
    }

    private static bool IsUnsafeIdCharacter(char c)
    {
        return char.IsControl(c) || char.IsWhiteSpace(c) || UnsafeIdCharacters.Contains(c);
    }

    private static string Text(JsonElement element, string name)
    {
        return element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : "";
    }

    private static string Header(string shell, string load)
    {
        return $"# TypeWhisper CLI completion for {shell}. Generated by: {CommandName} completions {shell}\n"
            + $"# Load it with: {load}\n\n";
    }

    private static string Quote(string value)
    {
        return $"'{value}'";
    }

    private static string Words(IEnumerable<string> values)
    {
        return string.Join(' ', values.Select(Quote));
    }

    // Shared by bash and zsh: positional parameters and case only, no arrays.
    private static string ShellCandidates()
    {
        var options = new StringBuilder();
        foreach (var group in s_commandOptions.GroupBy(item => string.Join(' ', item.Options)))
        {
            options.Append(
                $"                {string.Join('|', group.Select(item => Quote(item.Context)))}) printf '%s\\n' {Words(group.First().Options)} ;;\n");
        }

        var positionals = new StringBuilder();
        foreach (var group in s_positionals.GroupBy(item => string.Join(' ', item.Entry)))
        {
            positionals.Append(
                $"        {string.Join('|', group.Select(item => Quote(item.Context)))}) printf '%s\\n' {Words(group.First().Entry)} ;;\n");
        }

        var values = new StringBuilder();
        foreach (var (option, optionValues) in s_optionValues)
        {
            values.Append($"            {Quote(option)}) printf '%s\\n' words {Words(optionValues)} ;;\n");
        }

        return Fill(
            """
            # Prints the completion kind (words, files or none), then the candidate words.
            # Arguments: the command, the word being completed and the words before it.
            __typewhisper_cli_candidates() {
                local exe="$1" cur="$2" cmd="" sub="" count=0 pending="" literal="" engine="" token="" key="" word=""
                shift 2
                for word in "$@"; do
                    if [ -n "$pending" ]; then
                        case "$pending" in
                            --engine) engine="$word" ;;
                            --token|--api-token) token="$word" ;;
                        esac
                        pending=""
                        continue
                    fi
                    if [ -z "$literal" ]; then
                        case "$word" in
                            --) literal=1; continue ;;
            ##VALUE_OPTIONS##) pending="$word"; continue ;;
                            -?*) continue ;;
                        esac
                    fi
                    count=$((count + 1))
                    if [ "$count" -eq 1 ]; then cmd="$word"; elif [ "$count" -eq 2 ]; then sub="$word"; fi
                done

                if [ -n "$pending" ]; then
                    case "$pending" in
                        --engine|--model)
                            printf '%s\n' words
                            if [ "$pending" = --engine ]; then
                                set -- __complete engines
                            else
                                set -- __complete models
                                if [ -n "$engine" ]; then set -- "$@" --engine "$engine"; fi
                            fi
                            if [ -n "$token" ]; then set -- "$@" --token "$token"; fi
                            "$exe" "$@" 2>/dev/null
                            ;;
            ##VALUES##
                        *) printf '%s\n' none ;;
                    esac
                    return 0
                fi

                if [ -z "$literal" ]; then
                    case "$cur" in
                        -*)
                            printf '%s\n' words
                            key="$cmd"
                            if [ "$count" -ge 2 ]; then
                                case "$cmd" in
                                    ##SUBCOMMAND_COMMANDS##) key="$cmd $sub" ;;
                                esac
                            fi
                            case "$key" in
            ##OPTIONS##
                            esac
                            printf '%s\n' ##GLOBAL_OPTIONS##
                            return 0
                            ;;
                    esac
                fi

                case "$count" in
                    0) key="/0" ;;
                    1) key="$cmd/1" ;;
                    *) key="" ;;
                esac
                case "$key" in
            ##POSITIONALS##
                    *) printf '%s\n' none ;;
                esac
            }

            """,
            ("VALUE_OPTIONS", $"                {string.Join('|', s_valueOptions)}"),
            ("SUBCOMMAND_COMMANDS", string.Join('|', s_subcommandCommands)),
            ("GLOBAL_OPTIONS", Words(s_globalOptions)),
            ("VALUES", values.ToString()),
            ("OPTIONS", options.ToString()),
            ("POSITIONALS", positionals.ToString()));
    }

    // Replaces ##NAME## tokens. A token alone on its line takes its line break with it, so
    // generated blocks keep their own indentation.
    private static string Fill(string template, params (string Name, string Value)[] values)
    {
        var filled = template.ReplaceLineEndings("\n");
        foreach (var (name, value) in values)
        {
            filled = filled
                .Replace($"\n##{name}##\n", "\n" + value, StringComparison.Ordinal)
                .Replace($"##{name}##", value, StringComparison.Ordinal);
        }

        return filled;
    }

    private const string BashGlue =
        """
        _typewhisper_cli() {
            local kind="" line="" word="" rest="" trimmed="" prefix="" breaks="" i=0 offset=0 glued=0 split=0
            local -a words=() candidates=()
            COMPREPLY=()
            # Bash splits words at ':' and '=', so a=b arrives as a, = and b. Rejoin words that no space separates,
            # and remember the text up to the last split: bash replaces only what follows it.
            if [[ "$COMP_WORDBREAKS" == *:* ]]; then breaks+=":"; fi
            if [[ "$COMP_WORDBREAKS" == *=* ]]; then breaks+="="; fi
            line="${COMP_LINE:0:COMP_POINT}"
            for ((i = 0; i <= COMP_CWORD; i++)); do
                word="${COMP_WORDS[i]}"
                rest="${line:offset}"
                trimmed="${rest#"${rest%%[![:space:]]*}"}"
                glued=0
                if [ "$i" -gt 0 ] && [ "${#trimmed}" -eq "${#rest}" ]; then glued=1; fi
                offset=$((offset + ${#rest} - ${#trimmed} + ${#word}))
                # A token made only of word-break characters is where bash split the word.
                split=0
                if [ -n "$breaks" ] && [ -n "$word" ] && [ -z "${word//[$breaks]/}" ]; then split=1; fi
                if [ "$glued" -eq 1 ] && { [ "$split" -eq 1 ] || { [ -n "$breaks" ] && [[ "${words[${#words[@]}-1]}" == *[$breaks] ]]; }; }; then
                    words[${#words[@]}-1]+="$word"
                else
                    words+=("$word")
                    prefix=""
                fi
                if [ "$split" -eq 1 ]; then prefix="${words[${#words[@]}-1]}"; fi
            done
            local cur="${words[${#words[@]}-1]}"
            # COMP_WORDS keeps the user's quoting, so --engine "my engine" must reach the helper unquoted.
            # Remove simple quotes and escapes without evaluating the words.
            local -a plain=()
            for word in "${words[@]:1:${#words[@]}-2}"; do
                case "$word" in
                    \"*\"|\'*\') word="${word:1:${#word}-2}" ;;
                    *) word="${word//\\/}" ;;
                esac
                plain+=("$word")
            done
            while IFS= read -r line; do
                if [ -z "$kind" ]; then kind="$line"; else candidates+=("$line"); fi
            done < <(__typewhisper_cli_candidates "${words[0]}" "$cur" ${plain[@]+"${plain[@]}"})
            case "$kind" in
                words)
                    # Literal prefix matching: compgen -W would expand IDs from the app as shell words.
                    for line in ${candidates[@]+"${candidates[@]}"}; do
                        if [[ "$line" == "$cur"* ]]; then COMPREPLY+=("$line"); fi
                    done
                    ;;
                files)
                    # COMP_WORDS keeps the user's quoting; look up the plain path without evaluating it.
                    # Readline knows an opened quote and quotes inserted file names itself.
                    case "$cur" in
                        \"*) word="${cur#\"}" ;;
                        \'*) word="${cur#\'}" ;;
                        *) word="${cur//\\/}" ;;
                    esac
                    type compopt >/dev/null 2>&1 && compopt -o filenames
                    while IFS= read -r line; do
                        COMPREPLY+=("$line")
                    done < <(compgen -f -- "$word")
                    ;;
            esac
            # Candidates are unquoted, so compare them with the unquoted text before the split.
            if [ -n "$prefix" ]; then
                prefix="${prefix//[\\\"\']/}"
                for ((i = 0; i < ${#COMPREPLY[@]}; i++)); do COMPREPLY[i]="${COMPREPLY[i]#"$prefix"}"; done
            fi
        }

        complete -F _typewhisper_cli typewhisper-cli

        """;

    // Loaded either from an fpath file named _typewhisper-cli (whose body runs as that function)
    // or sourced after compinit.
    private const string ZshGlue =
        """
        _typewhisper_cli() {
            local -a candidates
            candidates=("${(@f)$(__typewhisper_cli_candidates "${(Q)words[1]}" "${(Q)PREFIX}" "${(@Q)words[2,CURRENT-1]}")}")
            case "$candidates[1]" in
                words) (( $#candidates > 1 )) && compadd -- "${(@)candidates[2,-1]}" ;;
                files) _files ;;
            esac
        }

        if [ "$funcstack[1]" = "_typewhisper-cli" ]; then
            _typewhisper_cli "$@"
        else
            compdef _typewhisper_cli typewhisper-cli
        fi

        """;

    private static string FishScript()
    {
        var options = new StringBuilder();
        foreach (var group in s_commandOptions.GroupBy(item => string.Join(' ', item.Options)))
        {
            options.Append(
                $"                case {string.Join(' ', group.Select(item => Quote(item.Context)))}\n                    printf '%s\\n' {Words(group.First().Options)}\n");
        }

        var positionals = new StringBuilder();
        foreach (var group in s_positionals.GroupBy(item => string.Join(' ', item.Entry)))
        {
            positionals.Append(
                $"        case {string.Join(' ', group.Select(item => Quote(item.Context)))}\n            printf '%s\\n' {Words(group.First().Entry)}\n");
        }

        var values = new StringBuilder();
        foreach (var (option, optionValues) in s_optionValues)
        {
            values.Append($"            case {Quote(option)}\n                printf '%s\\n' words {Words(optionValues)}\n");
        }

        return Fill(
            """
            # Prints the completion kind (words, files or none), then the candidate words.
            # Arguments: the command, the word being completed and the words before it.
            function __typewhisper_cli_candidates
                set -l exe $argv[1]
                set -l cur $argv[2]
                set -l cmd ''
                set -l sub ''
                set -l count 0
                set -l pending ''
                set -l literal ''
                set -l engine ''
                set -l token ''
                set -e argv[1]
                set -e argv[1]
                for word in $argv
                    if test -n "$pending"
                        switch $pending
                            case --engine
                                set engine $word
                            case --token --api-token
                                set token $word
                        end
                        set pending ''
                        continue
                    end
                    if test -z "$literal"
                        switch $word
                            case --
                                set literal 1
                                continue
                            case ##VALUE_OPTIONS##
                                set pending $word
                                continue
                            case '-?*'
                                continue
                        end
                    end
                    set count (math $count + 1)
                    if test $count -eq 1
                        set cmd $word
                    else if test $count -eq 2
                        set sub $word
                    end
                end

                if test -n "$pending"
                    switch $pending
                        case --engine --model
                            printf '%s\n' words
                            set -l arguments __complete engines
                            if test $pending = --model
                                set arguments __complete models
                                test -n "$engine"; and set -a arguments --engine $engine
                            end
                            test -n "$token"; and set -a arguments --token $token
                            $exe $arguments 2>/dev/null
            ##VALUES##
                        case '*'
                            printf '%s\n' none
                    end
                    return 0
                end

                if test -z "$literal"; and string match -q -- '-*' $cur
                    printf '%s\n' words
                    set -l key $cmd
                    if test $count -ge 2; and contains -- $cmd ##SUBCOMMAND_COMMANDS##
                        set key "$cmd $sub"
                    end
                    switch $key
            ##OPTIONS##
                    end
                    printf '%s\n' ##GLOBAL_OPTIONS##
                    return 0
                end

                set -l key ''
                switch $count
                    case 0
                        set key /0
                    case 1
                        set key "$cmd/1"
                end
                switch $key
            ##POSITIONALS##
                    case '*'
                        printf '%s\n' none
                end
            end

            function __typewhisper_cli_complete
                set -l tokens (commandline -opc)
                set -l cur (commandline -ct)
                set -l exe $tokens[1]
                set -e tokens[1]
                set -l candidates (__typewhisper_cli_candidates $exe "$cur" $tokens)
                set -l kind $candidates[1]
                set -e candidates[1]
                switch "$kind"
                    case words
                        printf '%s\n' $candidates
                    case files
                        __fish_complete_path "$cur"
                end
            end

            complete -c typewhisper-cli -f -a '(__typewhisper_cli_complete)'

            """,
            ("VALUE_OPTIONS", string.Join(' ', s_valueOptions)),
            ("SUBCOMMAND_COMMANDS", string.Join(' ', s_subcommandCommands)),
            ("GLOBAL_OPTIONS", Words(s_globalOptions)),
            ("VALUES", values.ToString()),
            ("OPTIONS", options.ToString()),
            ("POSITIONALS", positionals.ToString()));
    }
}
