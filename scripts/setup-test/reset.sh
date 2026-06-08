#!/usr/bin/env bash
#
# reset.sh — undo everything the TypeWhisper setup did, back to the baseline.
#
# Run this after a test pass. It removes TypeWhisper-created files/units/shortcuts
# and, using the baseline captured by snapshot.sh, uninstalls only the packages
# and the GNOME extension that the setup itself added (never anything that was
# already present).
#
#   ./reset.sh --dry-run     # show exactly what would be removed, change nothing
#   ./reset.sh               # do it (asks for confirmation)
#   ./reset.sh --yes         # do it without the confirmation prompt
#   ./reset.sh --keep-models # preserve downloaded models (~/.local/share/TypeWhisper/Models)
#   ./reset.sh --keep-packages   # don't uninstall any packages
#
# Safe by design: only files carrying the "Installed by TypeWhisper" marker, or
# living at exclusively-TypeWhisper paths, are deleted; packages/extension
# removal is gated on the baseline.

set -uo pipefail
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck source=lib.sh
source "$SCRIPT_DIR/lib.sh"

DRY_RUN=0; ASSUME_YES=0; KEEP_MODELS=0; KEEP_PACKAGES=0
for arg in "$@"; do
    case "$arg" in
        --dry-run)       DRY_RUN=1 ;;
        --yes|-y)        ASSUME_YES=1 ;;
        --keep-models)   KEEP_MODELS=1 ;;
        --keep-packages) KEEP_PACKAGES=1 ;;
        -h|--help)       grep '^#' "$0" | sed 's/^# \{0,1\}//'; exit 0 ;;
        *) err "unknown option: $arg"; exit 2 ;;
    esac
done

# act <message> <command...> : always prints the message; runs the command only
# when not in dry-run. Read-only probing happens outside act.
act() {
    local msg="$1"; shift
    if [[ $DRY_RUN == 1 ]]; then
        printf '   %swould:%s %s\n' "$c_ylw" "$c_reset" "$msg"
    else
        printf '   %s\n' "$msg"
        "$@"
    fi
}

file_is_ours() { [[ -f "$1" ]] && grep -q "$MARKER" "$1" 2>/dev/null; }

# ---- preamble --------------------------------------------------------------

head1 "TypeWhisper setup-test — reset$([[ $DRY_RUN == 1 ]] && echo ' (dry-run)')"

if [[ ! -f "$BASELINE_ENV" ]]; then
    err "No baseline found at $BASELINE_ENV"
    err "Run scripts/setup-test/snapshot.sh on a clean machine first, so reset"
    err "knows which packages/extension were pre-existing and must be kept."
    exit 1
fi
# shellcheck source=/dev/null
source "$BASELINE_ENV"
info "Baseline from ${BASELINE_DATE:-unknown} (pkg manager: ${PKG_MGR:-unknown})"

if [[ $DRY_RUN == 0 && $ASSUME_YES == 0 ]]; then
    printf '%sThis will remove TypeWhisper setup artifacts from this machine.%s\n' "$c_bold" "$c_reset"
    read -r -p "Proceed? [y/N] " reply
    [[ "$reply" =~ ^[Yy]$ ]] || { warn "aborted"; exit 0; }
fi

# ---- 1. ydotool service + unit + udev rule ---------------------------------

head1 "ydotool"
if command -v systemctl >/dev/null 2>&1; then
    if systemctl --user is-enabled ydotoold.service >/dev/null 2>&1 \
       || systemctl --user is-active ydotoold.service >/dev/null 2>&1; then
        act "systemctl --user disable --now ydotoold.service" \
            systemctl --user disable --now ydotoold.service
    else
        ok "ydotoold user service not active"
    fi
fi
if file_is_ours "$YDOTOOL_UNIT"; then
    act "rm -f $YDOTOOL_UNIT" rm -f "$YDOTOOL_UNIT"
    command -v systemctl >/dev/null 2>&1 && act "systemctl --user daemon-reload" systemctl --user daemon-reload
elif [[ -f "$YDOTOOL_UNIT" ]]; then
    warn "left $YDOTOOL_UNIT (no TypeWhisper marker — not ours)"
else
    ok "no ydotoold user unit"
fi
if file_is_ours "$YDOTOOL_UDEV_RULE"; then
    act "pkexec rm -f $YDOTOOL_UDEV_RULE" pkexec rm -f "$YDOTOOL_UDEV_RULE"
    act "reload udev rules" pkexec sh -c 'udevadm control --reload && udevadm trigger --subsystem-match=misc --action=change'
elif [[ -f "$YDOTOOL_UDEV_RULE" ]]; then
    warn "left $YDOTOOL_UDEV_RULE (no TypeWhisper marker — not ours)"
else
    ok "no ydotool udev rule"
fi

# ---- 2. GNOME custom keybinding --------------------------------------------

head1 "GNOME shortcut"
if command -v gsettings >/dev/null 2>&1 && command -v python3 >/dev/null 2>&1; then
    kb=$(gsettings get org.gnome.settings-daemon.plugins.media-keys custom-keybindings 2>/dev/null || echo "")
    if [[ "$kb" == *typewhisper-* ]]; then
        act "remove typewhisper-* custom keybindings + reset their keys" python3 - <<'PY'
import subprocess, ast
schema = "org.gnome.settings-daemon.plugins.media-keys"
key = "custom-keybindings"
raw = subprocess.check_output(["gsettings", "get", schema, key]).decode().strip()
if raw.startswith("@as"):
    raw = raw[3:].strip()
items = ast.literal_eval(raw) if raw and raw != "[]" else []
keep = [p for p in items if "typewhisper-" not in p]
drop = [p for p in items if "typewhisper-" in p]
for p in drop:
    sp = f"{schema}.custom-keybinding:{p}"
    for k in ("name", "command", "binding"):
        subprocess.run(["gsettings", "reset", sp, k], check=False)
val = "[" + ", ".join("'" + p + "'" for p in keep) + "]"
subprocess.run(["gsettings", "set", schema, key, val], check=True)
print(f"   removed {len(drop)} TypeWhisper keybinding(s)")
PY
    else
        ok "no TypeWhisper GNOME shortcut registered"
    fi
elif [[ "$(gsettings get org.gnome.settings-daemon.plugins.media-keys custom-keybindings 2>/dev/null)" == *typewhisper-* ]]; then
    warn "python3 not available — remove the typewhisper-* entry manually via dconf-editor"
fi

# ---- 3. marker'd / exclusively-named files ---------------------------------

head1 "Config files"
# CLI launcher (~/.local/bin/typewhisper) — points at our install dir.
if [[ -f "$CLI_LAUNCHER" ]] && grep -q "TypeWhisper" "$CLI_LAUNCHER" 2>/dev/null; then
    act "rm -f $CLI_LAUNCHER" rm -f "$CLI_LAUNCHER"
elif [[ -e "$CLI_LAUNCHER" ]]; then
    warn "left $CLI_LAUNCHER (does not reference TypeWhisper)"
else
    ok "no CLI launcher"
fi
# Autostart entry (exclusively ours by name).
[[ -f "$AUTOSTART_FILE" ]] && act "rm -f $AUTOSTART_FILE" rm -f "$AUTOSTART_FILE" || ok "no autostart entry"
# environment.d accessibility conf (marker'd).
if file_is_ours "$ENVIRONMENTD_FILE"; then
    act "rm -f $ENVIRONMENTD_FILE" rm -f "$ENVIRONMENTD_FILE"
else
    ok "no TypeWhisper environment.d conf"
fi
# KDE kglobalaccel entries (typewhisper-*.desktop).
if [[ -d "$KGLOBALACCEL_DIR" ]]; then
    while IFS= read -r f; do
        [[ -n "$f" ]] && act "rm -f $f" rm -f "$f"
    done < <(find "$KGLOBALACCEL_DIR" -maxdepth 1 -name 'typewhisper*.desktop' 2>/dev/null)
fi
# Browser launcher overrides written into ~/.local/share/applications (marker'd).
APPLICATIONS_DIR="$XDG_DATA_HOME_RESOLVED/applications"
if [[ -d "$APPLICATIONS_DIR" ]]; then
    while IFS= read -r f; do
        if file_is_ours "$f"; then act "rm -f $f (TypeWhisper-marked launcher override)" rm -f "$f"; fi
    done < <(find "$APPLICATIONS_DIR" -maxdepth 1 -name '*.desktop' 2>/dev/null)
fi
# Firefox user.js accessibility pref (best-effort; only files carrying the marker).
shopt -s nullglob
for userjs in "$HOME"/.mozilla/firefox/*/user.js "$XDG_CONFIG_HOME_RESOLVED"/mozilla/firefox/*/user.js \
              "$HOME"/.librewolf/*/user.js "$HOME"/.zen/*/user.js; do
    if file_is_ours "$userjs"; then
        act "strip TypeWhisper prefs from $userjs" \
            bash -c 'grep -v "Installed by TypeWhisper" "$1" | grep -v "accessibility.force_disabled" > "$1.tw_tmp" && mv "$1.tw_tmp" "$1"' _ "$userjs"
    fi
done
shopt -u nullglob

# ---- 4. app data dir -------------------------------------------------------

head1 "App data ($APP_DATA_DIR)"
if [[ -d "$APP_DATA_DIR" ]]; then
    if [[ $KEEP_MODELS == 1 ]]; then
        info "keeping models: $APP_MODELS_DIR"
        while IFS= read -r entry; do
            [[ "$(basename "$entry")" == "Models" ]] && continue
            act "rm -rf $entry" rm -rf "$entry"
        done < <(find "$APP_DATA_DIR" -mindepth 1 -maxdepth 1 2>/dev/null)
    else
        act "rm -rf $APP_DATA_DIR" rm -rf "$APP_DATA_DIR"
    fi
else
    ok "no app data dir"
fi

# ---- 5. packages -----------------------------------------------------------

head1 "Packages"
if [[ $KEEP_PACKAGES == 1 ]]; then
    info "--keep-packages: skipping package removal"
elif [[ ! -f "$BASELINE_PKGS_ABSENT" ]]; then
    warn "no baseline package list — skipping package removal"
else
    mgr="${PKG_MGR:-$(detect_pkg_mgr)}"
    to_remove=()
    while IFS= read -r pkg; do
        [[ -z "$pkg" ]] && continue
        if pkg_installed "$mgr" "$pkg"; then to_remove+=("$pkg"); fi
    done < "$BASELINE_PKGS_ABSENT"
    if [[ ${#to_remove[@]} -eq 0 ]]; then
        ok "no setup-added packages to remove"
    else
        prefix=$(pkg_remove_cmd_prefix "$mgr")
        if [[ -z "$prefix" ]]; then
            warn "unknown package manager — remove manually: ${to_remove[*]}"
        else
            act "$prefix ${to_remove[*]}" bash -c "$prefix \"\$@\"" _ "${to_remove[@]}"
        fi
    fi
fi

# ---- 6. Window Calls extension --------------------------------------------

head1 "GNOME Window Calls extension"
if [[ "${WINDOW_CALLS_AT_BASELINE:-present}" == "present" ]]; then
    ok "was present at baseline — leaving it installed"
elif ! window_calls_present; then
    ok "not installed"
elif command -v gnome-extensions >/dev/null 2>&1; then
    uuid=$(gnome-extensions list 2>/dev/null | grep -i 'window-calls' | head -n1)
    if [[ -n "$uuid" ]]; then
        act "gnome-extensions uninstall $uuid" gnome-extensions uninstall "$uuid"
    else
        warn "Window Calls is active but not a user extension — uninstall it from your browser/extensions app"
    fi
else
    warn "gnome-extensions CLI missing — uninstall Window Calls manually"
fi

head1 "Done"
[[ $DRY_RUN == 1 ]] && warn "dry-run: nothing was changed." || ok "Reset complete. Re-run snapshot.sh only if the machine state changed for good reasons."
