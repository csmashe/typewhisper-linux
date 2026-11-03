#!/usr/bin/env bash
# Install TypeWhisper from its tarball into the current user's profile.
# No root or network access is required.
#
# Usage:
#   ./install.sh                       Install or update TypeWhisper.
#   ./install.sh --uninstall           Remove only the recorded app artifacts.
#   ./install.sh --uninstall --purge   Also remove recordings, history, keys,
#                                      models, settings, and other user data.
set -euo pipefail

[ -n "${HOME:-}" ] || { printf 'HOME must be set.\n' >&2; exit 1; }

HERE="$(cd "$(dirname "$0")" && pwd -P)"
LIBRARY="$HERE/lib/managed-artifacts.sh"
if [ ! -r "$LIBRARY" ]; then
  printf 'Managed-artifact library is missing: %s\n' "$LIBRARY" >&2
  exit 1
fi
# shellcheck source=lib/managed-artifacts.sh
source "$LIBRARY"

DATA_HOME="${XDG_DATA_HOME:-$HOME/.local/share}"
STATE_HOME="${XDG_STATE_HOME:-$HOME/.local/state}"
DATA_DIR="$DATA_HOME/TypeWhisper"
INSTALL_ROOT="$DATA_HOME/typewhisper-app"
APPS_DIR="$DATA_HOME/applications"
ICONS_DIR="$DATA_HOME/icons/hicolor/128x128/apps"
BIN_DIR="$HOME/.local/bin"
DESKTOP_FILE="$APPS_DIR/typewhisper.desktop"
ICON_FILE="$ICONS_DIR/typewhisper.png"
BIN_LINK="$BIN_DIR/typewhisper"
STATE_DIR="$STATE_HOME/typewhisper/installer"

ACTION=install
PURGE=0
case "${1:-}" in
  "") ;;
  --uninstall)
    ACTION=remove
    case "${2:-}" in
      "") ;;
      --purge|--all) PURGE=1 ;;
      *) printf 'Unknown option: %s (expected --purge/--all)\n' "$2" >&2; exit 2 ;;
    esac
    [ "$#" -le 2 ] || { printf 'Too many arguments.\n' >&2; exit 2; }
    ;;
  -h|--help)
    sed -n '2,9s/^# \{0,1\}//p' "$0"
    exit 0
    ;;
  *) printf 'Unknown option: %s\n' "$1" >&2; exit 2 ;;
esac

case "$DATA_DIR" in
  ""|/|"$HOME"|"$HOME/")
    printf "Refusing to continue: DATA_DIR ('%s') is unsafe.\n" "$DATA_DIR" >&2
    exit 1
    ;;
esac
case "$INSTALL_ROOT" in
  ""|/|"$HOME"|"$HOME/"|"$DATA_DIR")
    printf "Refusing to continue: INSTALL_ROOT ('%s') is unsafe.\n" "$INSTALL_ROOT" >&2
    exit 1
    ;;
esac

ma_initialize typewhisper "$STATE_DIR"
if [ "$ACTION" = install ]; then
  [ -x "$HERE/typewhisper" ] || {
    printf 'Tarball payload is missing its executable: %s\n' "$HERE/typewhisper" >&2
    exit 1
  }
  [ -f "$HERE/typewhisper.png" ] || {
    printf 'Tarball payload is missing its icon: %s\n' "$HERE/typewhisper.png" >&2
    exit 1
  }

  TEMP_DIR="$(mktemp -d "${TMPDIR:-/tmp}/typewhisper-tarball-install.XXXXXX")"
  cleanup_temp() {
    rm -rf -- "$TEMP_DIR"
  }
  trap cleanup_temp EXIT
  DESKTOP_SOURCE="$TEMP_DIR/typewhisper.desktop"
  cat >"$DESKTOP_SOURCE" <<DESKTOP
[Desktop Entry]
Type=Application
Version=1.0
Name=TypeWhisper
GenericName=Voice-to-text dictation
Comment=Speech-to-text dictation for Linux desktop
Exec=$INSTALL_ROOT/typewhisper
Icon=typewhisper
Terminal=false
Categories=Utility;Accessibility;AudioVideo;
StartupNotify=true
StartupWMClass=typewhisper
DESKTOP

  ma_register_directory app "$INSTALL_ROOT" "$HERE"
  ma_register_file desktop "$DESKTOP_FILE" "$DESKTOP_SOURCE" 0644
  ma_register_file icon "$ICON_FILE" "$HERE/typewhisper.png" 0644
  ma_register_link launcher "$BIN_LINK" "$INSTALL_ROOT/typewhisper"
  # Installs predating the manifest carry no record, so adopt them on ownership
  # evidence rather than refusing every upgrade as foreign.
  ma_register_adoption app payload
  ma_register_adoption desktop desktop "$INSTALL_ROOT"
  ma_register_adoption icon icon "$DESKTOP_FILE"
  ma_register_adoption launcher link-into "$INSTALL_ROOT"
  ma_install
else
  ma_register_directory app "$INSTALL_ROOT"
  ma_register_file desktop "$DESKTOP_FILE"
  ma_register_file icon "$ICON_FILE"
  ma_register_link launcher "$BIN_LINK" "$INSTALL_ROOT/typewhisper"
  ma_register_adoption app payload
  ma_register_adoption desktop desktop "$INSTALL_ROOT"
  ma_register_adoption icon icon "$DESKTOP_FILE"
  ma_register_adoption launcher link-into "$INSTALL_ROOT"
  ma_remove
  if [ "$PURGE" -eq 1 ]; then
    rm -rf -- "$DATA_DIR"
  fi
fi

if command -v update-desktop-database >/dev/null 2>&1; then
  update-desktop-database "$APPS_DIR" >/dev/null 2>&1 || true
fi
if command -v gtk-update-icon-cache >/dev/null 2>&1; then
  gtk-update-icon-cache "$DATA_HOME/icons/hicolor" >/dev/null 2>&1 || true
fi

if [ "$ACTION" = install ]; then
  printf 'TypeWhisper installed to %s\n' "$INSTALL_ROOT"
  printf 'Launch from your menu, or run: typewhisper\n'
elif [ "$PURGE" -eq 1 ]; then
  printf 'TypeWhisper and all its user data have been uninstalled.\n'
else
  printf 'TypeWhisper uninstalled. %s.\n' "$MA_LAST_MESSAGE"
  if [ -d "$DATA_DIR" ]; then
    printf 'Your recordings, history, backups, keys, and settings were kept at:\n  %s\n' "$DATA_DIR"
    printf "Re-run with '--uninstall --purge' to delete that data too.\n"
  fi
fi
