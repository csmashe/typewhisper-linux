using System.Text;

namespace TypeWhisper.Linux.Services.ManagedArtifacts;

internal sealed record PrivilegedManagedFileSpec(
    string ArtifactId,
    string DestinationPath,
    string DesiredContent,
    int ConflictExitCode,
    string ConflictToken,
    int SymlinkExitCode,
    string SymlinkToken,
    PrivilegedEquivalentProbe EquivalentProbe = PrivilegedEquivalentProbe.None
);

internal enum PrivilegedEquivalentProbe
{
    None,
    YdotoolModulesLoad,
    YdotoolUdevRule,
}

internal sealed record PrivilegedManagedFileTestHooks(
    string? AfterJournalsShell = null,
    IReadOnlyDictionary<int, string>? AfterPublishShell = null
);

/// <summary>
///     Generates a root-side, flock-serialized exact-image transaction. Every
///     classification and publication occurs inside the privileged shell.
/// </summary>
internal static class PrivilegedManagedFileTransaction
{
    internal const int FlockUnavailableExitCode = 72;
    internal const string FlockUnavailableToken = "TYPEWHISPER_FLOCK_UNAVAILABLE";

    public static string BuildInstallScript(
        string stateRoot,
        IReadOnlyList<PrivilegedManagedFileSpec> specs,
        string afterCommitShell,
        PrivilegedManagedFileTestHooks? testHooks = null
    )
    {
        Validate(stateRoot, specs);
        var script = new StringBuilder(CommonPrefix(stateRoot));
        AppendStages(script, specs);
        script.Append("committed=0\n");
        script.Append("cleanup_all() {\n");
        script.Append("  status=$?\n");
        script.Append("  if [ \"$committed\" -ne 1 ]; then\n");
        foreach (var index in Enumerable.Range(0, specs.Count))
        {
            script.Append($"    rollback_artifact \"$artifact_{index}\" \"$path_{index}\" || true\n");
        }

        script.Append("  fi\n");
        foreach (var index in Enumerable.Range(0, specs.Count))
        {
            script.Append($"  rm -f \"${{stage_{index}:-}}\" 2>/dev/null || true\n");
        }

        script.Append("  exit \"$status\"\n");
        script.Append("}\n");
        script.Append("trap cleanup_all EXIT\n");
        script.Append("trap 'exit 129' HUP\n");
        script.Append("trap 'exit 130' INT\n");
        script.Append("trap 'exit 143' TERM\n");

        foreach (var index in Enumerable.Range(0, specs.Count))
        {
            var spec = specs[index];
            script.Append(
                $"prepare_install \"$artifact_{index}\" \"$path_{index}\" \"$stage_{index}\" "
                + $"'{EquivalentName(spec.EquivalentProbe)}' {spec.ConflictExitCode} "
                + $"{ShellQuote(spec.ConflictToken)} {spec.SymlinkExitCode} {ShellQuote(spec.SymlinkToken)}\n"
            );
            script.Append($"action_{index}=$prepared_action\n");
        }

        foreach (var index in Enumerable.Range(0, specs.Count))
        {
            script.Append($"if [ \"$action_{index}\" = write ]; then\n");
            script.Append($"  begin_install \"$artifact_{index}\" \"$path_{index}\" \"$stage_{index}\"\n");
            script.Append("fi\n");
        }

        if (!string.IsNullOrEmpty(testHooks?.AfterJournalsShell))
        {
            script.Append(testHooks.AfterJournalsShell).Append('\n');
        }

        foreach (var index in Enumerable.Range(0, specs.Count))
        {
            script.Append($"if [ \"$action_{index}\" = write ]; then\n");
            script.Append($"  publish_install \"$artifact_{index}\" \"$path_{index}\" \"$stage_{index}\"\n");
            script.Append($"  stage_{index}=\n");
            script.Append("fi\n");
            if (testHooks?.AfterPublishShell?.TryGetValue(index, out var hook) == true)
            {
                script.Append(hook).Append('\n');
            }
        }

        foreach (var index in Enumerable.Range(0, specs.Count))
        {
            script.Append($"if [ \"$action_{index}\" = write ]; then\n");
            script.Append($"  finalize_install \"$artifact_{index}\" \"$path_{index}\"\n");
            script.Append("else\n");
            script.Append($"  clear_staging \"$artifact_{index}\" \"$path_{index}\" ''\n");
            script.Append("fi\n");
        }

        script.Append("committed=1\n");
        script.Append("trap - EXIT HUP INT TERM\n");
        script.Append("cleanup_stages\n");
        script.Append(afterCommitShell);
        if (!afterCommitShell.EndsWith('\n'))
        {
            script.Append('\n');
        }

        return script.ToString();
    }

    public static string BuildRemoveScript(
        string stateRoot,
        IReadOnlyList<PrivilegedManagedFileSpec> specs,
        string afterCommitShell,
        PrivilegedManagedFileTestHooks? testHooks = null
    )
    {
        Validate(stateRoot, specs);
        var script = new StringBuilder(CommonPrefix(stateRoot));
        AppendDesiredImages(script, specs);
        script.Append("committed=0\n");
        script.Append("cleanup_all() {\n");
        script.Append("  status=$?\n");
        script.Append("  if [ \"$committed\" -ne 1 ]; then\n");
        foreach (var index in Enumerable.Range(0, specs.Count))
        {
            script.Append($"    rollback_artifact \"$artifact_{index}\" \"$path_{index}\" || true\n");
        }

        script.Append("  fi\n");
        foreach (var index in Enumerable.Range(0, specs.Count))
        {
            script.Append($"  rm -f \"${{stage_{index}:-}}\" 2>/dev/null || true\n");
        }

        script.Append("  exit \"$status\"\n");
        script.Append("}\n");
        script.Append("trap cleanup_all EXIT\n");
        script.Append("trap 'exit 129' HUP\n");
        script.Append("trap 'exit 130' INT\n");
        script.Append("trap 'exit 143' TERM\n");

        foreach (var index in Enumerable.Range(0, specs.Count))
        {
            var spec = specs[index];
            script.Append(
                $"prepare_remove \"$artifact_{index}\" \"$path_{index}\" \"$stage_{index}\" "
                + $"'{EquivalentName(spec.EquivalentProbe)}' {spec.ConflictExitCode} "
                + $"{ShellQuote(spec.ConflictToken)} {spec.SymlinkExitCode} {ShellQuote(spec.SymlinkToken)}\n"
            );
            script.Append($"action_{index}=$prepared_action\n");
        }

        foreach (var index in Enumerable.Range(0, specs.Count))
        {
            script.Append($"if [ \"$action_{index}\" = remove ]; then\n");
            script.Append($"  begin_remove \"$artifact_{index}\" \"$path_{index}\"\n");
            script.Append("fi\n");
        }

        if (!string.IsNullOrEmpty(testHooks?.AfterJournalsShell))
        {
            script.Append(testHooks.AfterJournalsShell).Append('\n');
        }

        foreach (var index in Enumerable.Range(0, specs.Count))
        {
            script.Append($"case \"$action_{index}\" in\n");
            script.Append($"  remove) publish_remove \"$artifact_{index}\" \"$path_{index}\" ;;\n");
            script.Append($"  clear) clear_state \"$artifact_{index}\" ;;\n");
            script.Append("esac\n");
            if (testHooks?.AfterPublishShell?.TryGetValue(index, out var hook) == true)
            {
                script.Append(hook).Append('\n');
            }
        }

        script.Append("committed=1\n");
        script.Append("trap - EXIT HUP INT TERM\n");
        script.Append("cleanup_stages\n");
        script.Append(afterCommitShell);
        if (!afterCommitShell.EndsWith('\n'))
        {
            script.Append('\n');
        }

        return script.ToString();
    }

    public static string QuoteAsShCArgument(string script)
    {
        return ShellQuote(script);
    }

    private static void AppendStages(
        StringBuilder script,
        IReadOnlyList<PrivilegedManagedFileSpec> specs
    )
    {
        AppendDesiredImages(script, specs);
    }

    private static void AppendDesiredImages(
        StringBuilder script,
        IReadOnlyList<PrivilegedManagedFileSpec> specs
    )
    {
        foreach (var index in Enumerable.Range(0, specs.Count))
        {
            script.Append($"stage_{index}=\n");
        }

        script.Append("cleanup_stages() {\n");
        foreach (var index in Enumerable.Range(0, specs.Count))
        {
            script.Append($"  rm -f \"${{stage_{index}:-}}\" 2>/dev/null || true\n");
        }

        script.Append("}\n");
        script.Append("trap cleanup_stages EXIT HUP INT TERM\n");

        for (var index = 0; index < specs.Count; index++)
        {
            var spec = specs[index];
            var delimiter = $"TYPEWHISPER_ARTIFACT_{index}_EOF";
            if (spec.DesiredContent.Split('\n').Contains(delimiter, StringComparer.Ordinal))
            {
                throw new ArgumentException("Desired content collides with shell delimiter.");
            }

            script.Append($"artifact_{index}={ShellQuote(spec.ArtifactId)}\n");
            script.Append($"path_{index}={ShellQuote(spec.DestinationPath)}\n");
            script.Append(
                $"ensure_artifact_dir \"$artifact_{index}\" {spec.ConflictExitCode} {ShellQuote(spec.ConflictToken)}\n"
            );
            // A SIGKILL can leave the previous attempt's sibling stage behind. Clean
            // its recorded path before replacing the record with this attempt's stage.
            script.Append($"clear_staging \"$artifact_{index}\" \"$path_{index}\" ''\n");
            script.Append($"stage_{index}=$(mktemp \"${{path_{index}}}.typewhisper.XXXXXX\")\n");
            script.Append(
                $"printf '%s\\n' \"$stage_{index}\" > \"$state_root/$artifact_{index}/staging.path\"\n"
            );
            script.Append(
                $"secure_state_file \"$state_root/$artifact_{index}/staging.path\"\n"
            );
            script.Append($"cat > \"$stage_{index}\" <<'{delimiter}'\n");
            script.Append(spec.DesiredContent);
            if (!spec.DesiredContent.EndsWith('\n'))
            {
                script.Append('\n');
            }

            script.Append(delimiter).Append('\n');
            script.Append($"secure_publication_stage \"$stage_{index}\"\n");
        }

    }

    private static string CommonPrefix(string stateRoot)
    {
        var header =
            "set -eu\n"
            + "if ! command -v flock >/dev/null 2>&1; then\n"
            + $"  echo '{FlockUnavailableToken}: flock is required for TypeWhisper managed root files' >&2\n"
            + $"  exit {FlockUnavailableExitCode}\n"
            + "fi\n"
            + $"state_root={ShellQuote(stateRoot)}\n";
        return header
               + """
            if [ -L "$state_root" ] || { [ -e "$state_root" ] && [ ! -d "$state_root" ]; }; then
              echo 'TYPEWHISPER_ROOT_STATE_UNSAFE' >&2
              exit 72
            fi
            mkdir -p "$state_root"
            chmod 0700 "$state_root"
            if [ -L "$state_root" ] || [ ! -d "$state_root" ]; then
              echo 'TYPEWHISPER_ROOT_STATE_UNSAFE' >&2
              exit 72
            fi
            exec 9>"$state_root/transaction.lock"
            chmod 0600 "$state_root/transaction.lock"
            flock -x 9

            mode_of() {
              stat -c '%a' "$1"
            }

            safe_regular() {
              [ ! -L "$1" ] && [ -f "$1" ]
            }

            sync_file() {
              sync -f "$1" 2>/dev/null || sync
            }

            secure_state_file() {
              chown root:root "$1"
              chmod 0600 "$1"
              [ "$(mode_of "$1")" = 600 ]
              sync_file "$1"
            }

            secure_publication_stage() {
              chown root:root "$1"
              chmod 0644 "$1"
              [ "$(mode_of "$1")" = 644 ]
              sync_file "$1"
            }

            ensure_artifact_dir() {
              artifact_dir="$state_root/$1"
              if [ -L "$artifact_dir" ] || { [ -e "$artifact_dir" ] && [ ! -d "$artifact_dir" ]; }; then
                echo "$3" >&2
                exit "$2"
              fi
              mkdir -p "$artifact_dir"
              chown root:root "$artifact_dir"
              chmod 0700 "$artifact_dir"
              [ "$(mode_of "$artifact_dir")" = 700 ]
            }

            clear_pending() {
              artifact_dir="$state_root/$1"
              rm -f "$artifact_dir/pending.operation" "$artifact_dir/pending.old" \
                "$artifact_dir/pending.old.exists" "$artifact_dir/pending.old.mode" \
                "$artifact_dir/pending.new"
            }

            clear_state() {
              artifact_dir="$state_root/$1"
              rm -f "$artifact_dir/current" "$artifact_dir/current.mode"
              clear_pending "$1"
              rm -f "$artifact_dir/staging.path"
            }

            clear_staging() {
              artifact="$1"; path="$2"; active_stage="$3"
              artifact_dir="$state_root/$artifact"
              if safe_regular "$artifact_dir/staging.path"; then
                recorded_stage=$(cat "$artifact_dir/staging.path")
                if [ "$recorded_stage" != "$active_stage" ]; then
                  case "$recorded_stage" in
                    "$path.typewhisper."*)
                      if safe_regular "$recorded_stage"; then rm -f "$recorded_stage"; fi
                      ;;
                  esac
                  rm -f "$artifact_dir/staging.path"
                fi
              fi
            }

            finalize_install() {
              artifact="$1"
              path="$2"
              active_stage="${3:-}"
              artifact_dir="$state_root/$artifact"
              current_tmp="$artifact_dir/current.tmp.$$"
              cp "$path" "$current_tmp"
              secure_state_file "$current_tmp"
              mv -f "$current_tmp" "$artifact_dir/current"
              printf '%s\n' '644' > "$artifact_dir/current.mode.tmp.$$"
              secure_state_file "$artifact_dir/current.mode.tmp.$$"
              mv -f "$artifact_dir/current.mode.tmp.$$" "$artifact_dir/current.mode"
              clear_pending "$artifact"
              clear_staging "$artifact" "$path" "$active_stage"
            }

            recover_artifact() {
              artifact="$1"
              path="$2"
              conflict_code="$3"
              conflict_token="$4"
              active_stage="$5"
              ensure_artifact_dir "$artifact" "$conflict_code" "$conflict_token"
              clear_staging "$artifact" "$path" "$active_stage"
              artifact_dir="$state_root/$artifact"
              [ -e "$artifact_dir/pending.operation" ] || return 0
              if ! safe_regular "$artifact_dir/pending.operation"; then
                echo "$conflict_token" >&2
                exit "$conflict_code"
              fi
              operation=$(cat "$artifact_dir/pending.operation")
              case "$operation" in
                install)
                  if safe_regular "$path" && safe_regular "$artifact_dir/pending.new" \
                    && cmp -s "$path" "$artifact_dir/pending.new" \
                    && [ "$(mode_of "$path")" = 644 ]; then
                    finalize_install "$artifact" "$path" "$active_stage"
                  elif [ -e "$artifact_dir/pending.old.exists" ]; then
                    if safe_regular "$path" && safe_regular "$artifact_dir/pending.old" \
                      && cmp -s "$path" "$artifact_dir/pending.old" \
                      && [ "$(mode_of "$path")" = "$(cat "$artifact_dir/pending.old.mode")" ]; then
                      clear_pending "$artifact"
                    else
                      echo "$conflict_token" >&2
                      exit "$conflict_code"
                    fi
                  elif [ ! -e "$path" ] && [ ! -L "$path" ]; then
                    clear_pending "$artifact"
                  else
                    echo "$conflict_token" >&2
                    exit "$conflict_code"
                  fi
                  ;;
                remove)
                  if [ ! -e "$path" ] && [ ! -L "$path" ]; then
                    clear_state "$artifact"
                  elif safe_regular "$path" && safe_regular "$artifact_dir/pending.old" \
                    && cmp -s "$path" "$artifact_dir/pending.old" \
                    && [ "$(mode_of "$path")" = "$(cat "$artifact_dir/pending.old.mode")" ]; then
                    clear_pending "$artifact"
                  else
                    echo "$conflict_token" >&2
                    exit "$conflict_code"
                  fi
                  ;;
                *)
                  echo "$conflict_token" >&2
                  exit "$conflict_code"
                  ;;
              esac
            }

            is_equivalent() {
              kind="$1"
              path="$2"
              case "$kind" in
                ydotool-modules)
                  grep -Eq '^[[:space:]]*uinput[[:space:]]*$' "$path"
                  ;;
                ydotool-udev)
                  grep -Fqx 'KERNEL=="uinput", TAG+="uaccess", GROUP="input", MODE="0660", OPTIONS+="static_node=uinput"' "$path"
                  ;;
                *) false ;;
              esac
            }

            prepare_install() {
              artifact="$1"; path="$2"; desired="$3"; equivalent="$4"
              conflict_code="$5"; conflict_token="$6"; symlink_code="$7"; symlink_token="$8"
              recover_artifact "$artifact" "$path" "$conflict_code" "$conflict_token" "$desired"
              artifact_dir="$state_root/$artifact"
              if [ -L "$path" ]; then
                echo "$symlink_token" >&2; exit "$symlink_code"
              elif [ -e "$path" ] && [ ! -f "$path" ]; then
                echo "$conflict_token" >&2; exit "$conflict_code"
              fi
              if [ -e "$artifact_dir/current" ]; then
                if ! safe_regular "$artifact_dir/current" || ! safe_regular "$artifact_dir/current.mode"; then
                  echo "$conflict_token" >&2; exit "$conflict_code"
                fi
                if [ ! -e "$path" ]; then
                  prepared_action=write; return
                fi
                if ! cmp -s "$path" "$artifact_dir/current" \
                  || [ "$(mode_of "$path")" != "$(cat "$artifact_dir/current.mode")" ]; then
                  echo "$conflict_token" >&2; exit "$conflict_code"
                fi
                prepared_action=write; return
              fi
              if [ ! -e "$path" ]; then
                prepared_action=write
              elif cmp -s "$path" "$desired"; then
                prepared_action=write
              elif is_equivalent "$equivalent" "$path"; then
                prepared_action=skip
              else
                echo "$conflict_token" >&2; exit "$conflict_code"
              fi
            }

            begin_install() {
              artifact="$1"; path="$2"; desired="$3"
              artifact_dir="$state_root/$artifact"
              clear_pending "$artifact"
              if [ -e "$path" ]; then
                cp "$path" "$artifact_dir/pending.old"
                secure_state_file "$artifact_dir/pending.old"
                mode_of "$path" > "$artifact_dir/pending.old.mode"
                secure_state_file "$artifact_dir/pending.old.mode"
                : > "$artifact_dir/pending.old.exists"
                secure_state_file "$artifact_dir/pending.old.exists"
              fi
              cp "$desired" "$artifact_dir/pending.new"
              secure_state_file "$artifact_dir/pending.new"
              printf '%s\n' install > "$artifact_dir/pending.operation"
              secure_state_file "$artifact_dir/pending.operation"
            }

            publish_install() {
              artifact="$1"; path="$2"; stage="$3"
              artifact_dir="$state_root/$artifact"
              if [ -e "$artifact_dir/pending.old.exists" ]; then
                safe_regular "$path" && cmp -s "$path" "$artifact_dir/pending.old" \
                  && [ "$(mode_of "$path")" = "$(cat "$artifact_dir/pending.old.mode")" ]
              else
                [ ! -e "$path" ] && [ ! -L "$path" ]
              fi
              mv -f "$stage" "$path"
              chown root:root "$path"
              chmod 0644 "$path"
              [ "$(mode_of "$path")" = 644 ]
              sync_file "$path"
            }

            prepare_remove() {
              artifact="$1"; path="$2"; desired="$3"; equivalent="$4"
              conflict_code="$5"; conflict_token="$6"; symlink_code="$7"; symlink_token="$8"
              recover_artifact "$artifact" "$path" "$conflict_code" "$conflict_token" "$desired"
              artifact_dir="$state_root/$artifact"
              if [ -L "$path" ]; then
                echo "$symlink_token" >&2; exit "$symlink_code"
              elif [ -e "$path" ] && [ ! -f "$path" ]; then
                echo "$conflict_token" >&2; exit "$conflict_code"
              elif [ ! -e "$path" ]; then
                prepared_action=clear; return
              fi
              if [ -e "$artifact_dir/current" ]; then
                if safe_regular "$artifact_dir/current" && safe_regular "$artifact_dir/current.mode" \
                  && cmp -s "$path" "$artifact_dir/current" \
                  && [ "$(mode_of "$path")" = "$(cat "$artifact_dir/current.mode")" ]; then
                  prepared_action=remove; return
                fi
                echo "$conflict_token" >&2; exit "$conflict_code"
              fi
              if cmp -s "$path" "$desired"; then
                prepared_action=remove
              elif is_equivalent "$equivalent" "$path"; then
                prepared_action=skip
              else
                echo "$conflict_token" >&2; exit "$conflict_code"
              fi
            }

            begin_remove() {
              artifact="$1"; path="$2"
              artifact_dir="$state_root/$artifact"
              clear_pending "$artifact"
              cp "$path" "$artifact_dir/pending.old"
              secure_state_file "$artifact_dir/pending.old"
              mode_of "$path" > "$artifact_dir/pending.old.mode"
              secure_state_file "$artifact_dir/pending.old.mode"
              : > "$artifact_dir/pending.old.exists"
              secure_state_file "$artifact_dir/pending.old.exists"
              printf '%s\n' remove > "$artifact_dir/pending.operation"
              secure_state_file "$artifact_dir/pending.operation"
            }

            publish_remove() {
              artifact="$1"; path="$2"
              artifact_dir="$state_root/$artifact"
              safe_regular "$path" && cmp -s "$path" "$artifact_dir/pending.old" \
                && [ "$(mode_of "$path")" = "$(cat "$artifact_dir/pending.old.mode")" ]
              rm -f "$path"
              clear_state "$artifact"
            }

            rollback_artifact() {
              artifact="$1"; path="$2"
              artifact_dir="$state_root/$artifact"
              [ -e "$artifact_dir/pending.operation" ] || return 0
              if safe_regular "$path" && [ -e "$artifact_dir/pending.new" ] \
                && cmp -s "$path" "$artifact_dir/pending.new"; then
                if [ -e "$artifact_dir/pending.old.exists" ]; then
                  rollback_tmp="${path}.rollback.$$"
                  cp "$artifact_dir/pending.old" "$rollback_tmp"
                  chown root:root "$rollback_tmp"
                  chmod "$(cat "$artifact_dir/pending.old.mode")" "$rollback_tmp"
                  mv -f "$rollback_tmp" "$path"
                else
                  rm -f "$path"
                fi
              fi
            }

            """;
    }

    private static string EquivalentName(PrivilegedEquivalentProbe probe)
    {
        return probe switch
        {
            PrivilegedEquivalentProbe.YdotoolModulesLoad => "ydotool-modules",
            PrivilegedEquivalentProbe.YdotoolUdevRule => "ydotool-udev",
            _ => "none",
        };
    }

    private static string ShellQuote(string value)
    {
        return "'" + value.Replace("'", "'\"'\"'", StringComparison.Ordinal) + "'";
    }

    private static void Validate(
        string stateRoot,
        IReadOnlyList<PrivilegedManagedFileSpec> specs
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stateRoot);
        if (!Path.IsPathFullyQualified(stateRoot) || specs.Count == 0)
        {
            throw new ArgumentException("Root state path must be absolute and specs non-empty.");
        }

        if (
            specs.Any(spec =>
                string.IsNullOrWhiteSpace(spec.ArtifactId)
                || spec.ArtifactId.Any(character =>
                    !(char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.')
                )
                // The id becomes a directory name under the root state path, so a
                // traversal id would take mkdir/chmod outside it.
                || spec.ArtifactId is "." or ".."
                || !Path.IsPathFullyQualified(spec.DestinationPath)
            )
        )
        {
            throw new ArgumentException("Privileged artifact specification is unsafe.");
        }
    }
}
