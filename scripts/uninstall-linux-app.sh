#!/usr/bin/env bash
set -euo pipefail

# Uninstall TypeWhisper from the current user's profile (companion to
# install-linux-app.sh). DATA SAFETY: the user's dictation/recorder recordings
# (Audio/), history + database (Data/), saved backups (backups/), plugin API
# keys (PluginData/) and configuration (settings.json, linux-preferences.json)
# all live in DATA_DIR = $XDG_DATA_HOME/TypeWhisper. install-linux-app.sh keeps
# the app BINARY in a SEPARATE dir (typewhisper-app), precisely so uninstall can
# remove the program without touching that irreplaceable data. This script MUST
# preserve DATA_DIR by default. An earlier version ran `rm -rf` on DATA_DIR and
# destroyed recordings, history and API keys with no recovery — never again.
#
# Usage:
#   uninstall-linux-app.sh            Remove the app; KEEP all user data.
#   uninstall-linux-app.sh --purge    Also remove DATA_DIR (recordings, history,
#                                     keys, models) — everything gone.
# Non-interactive: no prompts, safe to run from scripts.

APP_ID="typewhisper"
DATA_DIR="${XDG_DATA_HOME:-$HOME/.local/share}/TypeWhisper"
APP_DIR="${XDG_DATA_HOME:-$HOME/.local/share}/typewhisper-app"
APPLICATIONS_DIR="${XDG_DATA_HOME:-$HOME/.local/share}/applications"
ICONS_DIR="${XDG_DATA_HOME:-$HOME/.local/share}/icons/hicolor/128x128/apps"
BIN_LINK="$HOME/.local/bin/$APP_ID"
DESKTOP_FILE="$APPLICATIONS_DIR/$APP_ID.desktop"
ICON_FILE="$ICONS_DIR/$APP_ID.png"

PURGE=0
case "${1:-}" in
  "") ;;
  --purge|--all)
    PURGE=1
    ;;
  -h|--help)
    grep -E '^#( |$)' "$0" | sed 's/^# \{0,1\}//'
    exit 0
    ;;
  *)
    echo "Unknown option: $1 (expected --purge/--all or nothing)" >&2
    exit 2
    ;;
esac

[ -n "${HOME:-}" ] || { echo "HOME must be set." >&2; exit 1; }

# Belt-and-suspenders: never let a mis-set variable point rm at HOME or /.
for guarded in "$DATA_DIR" "$APP_DIR"; do
  case "$guarded" in
    ""|"/"|"$HOME"|"$HOME/")
      echo "Refusing to uninstall: a target path ('$guarded') is unsafe to remove." >&2
      exit 1
      ;;
  esac
done

# Remove the app binary payload, launcher, icon and CLI symlink. None of these
# hold user data; install-linux-app.sh keeps the binary in APP_DIR, distinct
# from DATA_DIR.
rm -rf "$APP_DIR"
rm -f "$DESKTOP_FILE"
rm -f "$ICON_FILE"
rm -f "$BIN_LINK"

# Some older/tarball installs put the binary INSIDE DATA_DIR. Sweep only the
# known published-payload artifacts (top level only) out of DATA_DIR without
# touching user data. We match binary/library names and the .NET runtime's own
# config files by suffix — never settings.json / linux-preferences.json, and
# never the user-data subdirs (Audio, Data, PluginData, backups, ...).
if [ -d "$DATA_DIR" ]; then
  rm -f "$DATA_DIR/$APP_ID" "$DATA_DIR/AppRun" \
        "$DATA_DIR/$APP_ID.png" "$DATA_DIR/$APP_ID.desktop" \
        "$DATA_DIR/typewhisper.runtimeconfig.json" "$DATA_DIR/typewhisper.deps.json" 2>/dev/null || true
  find "$DATA_DIR" -maxdepth 1 -type f \
    \( -name '*.dll' -o -name '*.so' -o -name '*.so.*' -o -name '*.pdb' \
       -o -name '*.runtimeconfig.json' -o -name '*.deps.json' \) -delete 2>/dev/null || true
fi

if [ "$PURGE" -eq 1 ]; then
  # User explicitly asked to remove everything, including recordings/history/keys.
  rm -rf "$DATA_DIR"
fi

if command -v update-desktop-database >/dev/null 2>&1; then
  update-desktop-database "$APPLICATIONS_DIR" >/dev/null 2>&1 || true
fi

if command -v gtk-update-icon-cache >/dev/null 2>&1; then
  gtk-update-icon-cache "${XDG_DATA_HOME:-$HOME/.local/share}/icons/hicolor" >/dev/null 2>&1 || true
fi

if [ "$PURGE" -eq 1 ]; then
  echo "TypeWhisper and all its user data have been removed from this user profile."
else
  echo "TypeWhisper has been removed from this user profile."
  if [ -d "$DATA_DIR" ]; then
    echo "Your recordings, history, backups and settings were KEPT at:"
    echo "  $DATA_DIR"
    echo "Run with --purge to delete that data too."
  fi
fi
