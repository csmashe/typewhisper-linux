#!/usr/bin/env bash
#
# snapshot.sh — capture the machine's pre-setup baseline.
#
# Run this ONCE on a clean machine BEFORE you exercise the TypeWhisper setup
# wizard. It records which target packages were already installed and whether
# the GNOME "Window Calls" extension was already present, so that reset.sh only
# undoes what the setup actually added — it will never uninstall a package or
# extension that was already there.
#
# Safe to re-run: it overwrites the previous baseline. Re-run it whenever the
# machine is genuinely back to a known-good clean state.

set -uo pipefail
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck source=lib.sh
source "$SCRIPT_DIR/lib.sh"

head1 "TypeWhisper setup-test — capturing baseline"

# Warn loudly if the machine doesn't actually look clean: a baseline taken
# after setup has already run would teach reset.sh to leave our artifacts in
# place. We don't block (you may have a legitimate reason), but you should know.
dirty=0
[[ -d "$APP_DATA_DIR" ]]            && { warn "app data dir already exists: $APP_DATA_DIR"; dirty=1; }
[[ -f "$YDOTOOL_UNIT" ]]           && { warn "ydotoold user unit already exists"; dirty=1; }
[[ -f "$YDOTOOL_UDEV_RULE" ]]      && { warn "ydotool udev rule already exists"; dirty=1; }
[[ -f "$AUTOSTART_FILE" ]]         && { warn "autostart entry already exists"; dirty=1; }
if command -v gsettings >/dev/null 2>&1; then
    kb=$(gsettings get org.gnome.settings-daemon.plugins.media-keys custom-keybindings 2>/dev/null || echo "")
    [[ "$kb" == *typewhisper-* ]]  && { warn "a TypeWhisper GNOME shortcut is already registered"; dirty=1; }
fi
if [[ "$dirty" == 1 ]]; then
    warn "Machine does not look pristine. If you've already tested, run reset.sh first,"
    warn "then re-run this snapshot. Continuing in 3s (Ctrl-C to abort)…"
    sleep 3
fi

mkdir -p "$BASELINE_DIR"

mgr=$(detect_pkg_mgr)
info "Package manager: $mgr"

# Record packages that are ABSENT now — these are the only ones reset may remove.
: > "$BASELINE_PKGS_ABSENT"
absent_list=()
present_list=()
for pkg in "${TARGET_PACKAGES[@]}"; do
    if pkg_installed "$mgr" "$pkg"; then
        present_list+=("$pkg")
    else
        echo "$pkg" >> "$BASELINE_PKGS_ABSENT"
        absent_list+=("$pkg")
    fi
done
ok "Already installed (will NOT be removed): ${present_list[*]:-none}"
info "Absent now (removable if setup adds them): ${absent_list[*]:-none}"

# Window Calls extension presence.
if window_calls_present; then
    wc_baseline=present
    ok "Window Calls extension: present (will NOT be removed)"
else
    wc_baseline=absent
    info "Window Calls extension: absent (removable if setup adds it)"
fi

# Did the app data dir already exist? (Used only to warn, not to gate removal —
# the TypeWhisper/ dir is exclusively ours.)
[[ -d "$APP_DATA_DIR" ]] && appdir_baseline=present || appdir_baseline=absent

{
    echo "# TypeWhisper setup-test baseline"
    echo "BASELINE_DATE=$(date -Iseconds)"
    echo "BASELINE_HOST=$(hostname)"
    echo "PKG_MGR=$mgr"
    echo "WINDOW_CALLS_AT_BASELINE=$wc_baseline"
    echo "APP_DATA_DIR_AT_BASELINE=$appdir_baseline"
} > "$BASELINE_ENV"

head1 "Baseline saved"
ok "$BASELINE_ENV"
ok "$BASELINE_PKGS_ABSENT"
info "Now run the setup wizard. When done testing, run: scripts/setup-test/reset.sh --dry-run"
