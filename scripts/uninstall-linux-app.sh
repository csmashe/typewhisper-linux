#!/usr/bin/env bash
# Remove TypeWhisper from the current user's profile.
#
# Usage:
#   uninstall-linux-app.sh            Remove only recorded application artifacts.
#   uninstall-linux-app.sh --purge    Also remove recordings, history, backups,
#                                     plugin keys, models, and settings.
set -euo pipefail

[ -n "${HOME:-}" ] || { printf 'HOME must be set.\n' >&2; exit 1; }

ROOT="$(cd "$(dirname "$0")/.." && pwd -P)"
# shellcheck source=lib/managed-artifacts.sh
source "$ROOT/scripts/lib/managed-artifacts.sh"

DATA_HOME="${XDG_DATA_HOME:-$HOME/.local/share}"
STATE_HOME="${XDG_STATE_HOME:-$HOME/.local/state}"
DATA_DIR="$DATA_HOME/TypeWhisper"
APP_DIR="$DATA_HOME/typewhisper-app"
APPLICATIONS_DIR="$DATA_HOME/applications"
ICONS_DIR="$DATA_HOME/icons/hicolor/128x128/apps"
BIN_LINK="$HOME/.local/bin/typewhisper"
DESKTOP_FILE="$APPLICATIONS_DIR/typewhisper.desktop"
ICON_FILE="$ICONS_DIR/typewhisper.png"
STATE_DIR="$STATE_HOME/typewhisper/installer"

PURGE=0
case "${1:-}" in
  "") ;;
  --purge|--all) PURGE=1 ;;
  -h|--help)
    sed -n '2,7s/^# \{0,1\}//p' "$0"
    exit 0
    ;;
  *)
    printf 'Unknown option: %s (expected --purge/--all or nothing)\n' "$1" >&2
    exit 2
    ;;
esac
[ "$#" -le 1 ] || { printf 'Too many arguments.\n' >&2; exit 2; }

for guarded in "$DATA_DIR" "$APP_DIR"; do
  case "$guarded" in
    ""|/|"$HOME"|"$HOME/")
      printf "Refusing to uninstall: target path ('%s') is unsafe.\n" "$guarded" >&2
      exit 1
      ;;
  esac
done
if [ "$APP_DIR" = "$DATA_DIR" ]; then
  printf 'Refusing to uninstall: application and user-data paths overlap.\n' >&2
  exit 1
fi

ma_initialize typewhisper "$STATE_DIR"
ma_register_directory app "$APP_DIR"
ma_register_file desktop "$DESKTOP_FILE"
ma_register_file icon "$ICON_FILE"
ma_register_link launcher "$BIN_LINK" "$APP_DIR/typewhisper"
# Installs predating the manifest carry no record, so ownership evidence is what
# authorizes their removal. Anything failing its probe is refused, not deleted.
ma_register_adoption app payload
ma_register_adoption desktop desktop "$APP_DIR"
ma_register_adoption icon icon "$DESKTOP_FILE"
ma_register_adoption launcher link-into "$APP_DIR"
ma_remove

if [ "$PURGE" -eq 1 ]; then
  # --purge is the sole path that removes user-created data.
  rm -rf -- "$DATA_DIR"
fi

if command -v update-desktop-database >/dev/null 2>&1; then
  update-desktop-database "$APPLICATIONS_DIR" >/dev/null 2>&1 || true
fi
if command -v gtk-update-icon-cache >/dev/null 2>&1; then
  gtk-update-icon-cache "$DATA_HOME/icons/hicolor" >/dev/null 2>&1 || true
fi

if [ "$PURGE" -eq 1 ]; then
  printf 'TypeWhisper and all its user data have been removed from this user profile.\n'
else
  printf 'TypeWhisper uninstall finished: %s.\n' "$MA_LAST_MESSAGE"
  if [ -d "$DATA_DIR" ]; then
    printf 'Your recordings, history, backups, keys, models, and settings were kept at:\n'
    printf '  %s\n' "$DATA_DIR"
    printf 'Run with --purge to delete that data too.\n'
  fi
fi
