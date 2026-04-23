#!/usr/bin/env bash
set -euo pipefail

APP_ID="typewhisper"
INSTALL_ROOT="${XDG_DATA_HOME:-$HOME/.local/share}/TypeWhisper"
APPLICATIONS_DIR="${XDG_DATA_HOME:-$HOME/.local/share}/applications"
ICONS_DIR="${XDG_DATA_HOME:-$HOME/.local/share}/icons/hicolor/128x128/apps"
DESKTOP_FILE="$APPLICATIONS_DIR/$APP_ID.desktop"
ICON_FILE="$ICONS_DIR/$APP_ID.png"

rm -f "$DESKTOP_FILE"
rm -f "$ICON_FILE"
rm -rf "$INSTALL_ROOT"

if command -v update-desktop-database >/dev/null 2>&1; then
  update-desktop-database "$APPLICATIONS_DIR" >/dev/null 2>&1 || true
fi

if command -v gtk-update-icon-cache >/dev/null 2>&1; then
  gtk-update-icon-cache "${XDG_DATA_HOME:-$HOME/.local/share}/icons/hicolor" >/dev/null 2>&1 || true
fi

echo "TypeWhisper has been removed from this user profile."
