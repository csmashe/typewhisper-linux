#!/usr/bin/env bash
set -euo pipefail

[ -n "${HOME:-}" ] || { printf 'HOME must be set.\n' >&2; exit 1; }

ROOT="$(cd "$(dirname "$0")/.." && pwd -P)"
# shellcheck source=lib/managed-artifacts.sh
source "$ROOT/scripts/lib/managed-artifacts.sh"

PROJECT="$ROOT/src/TypeWhisper.Linux/TypeWhisper.Linux.csproj"
CONFIG="${CONFIGURATION:-Release}"
RID="${RID:-linux-x64}"
APP_NAME="TypeWhisper"
PUBLISH_DIR="$ROOT/src/TypeWhisper.Linux/bin/$CONFIG/net10.0/$RID/publish"
DATA_HOME="${XDG_DATA_HOME:-$HOME/.local/share}"
STATE_HOME="${XDG_STATE_HOME:-$HOME/.local/state}"

# Program payload and user data intentionally use different directories. The
# transaction owns only the lower-case app directory; recordings, history,
# downloaded models, API keys, and settings stay in the upper-case data path.
DATA_DIR="$DATA_HOME/TypeWhisper"
APP_DIR="$DATA_HOME/typewhisper-app"
APPLICATIONS_DIR="$DATA_HOME/applications"
ICONS_DIR="$DATA_HOME/icons/hicolor/128x128/apps"
BIN_DIR="$HOME/.local/bin"
DESKTOP_FILE="$APPLICATIONS_DIR/typewhisper.desktop"
ICON_SOURCE="$ROOT/src/TypeWhisper.Linux/Resources/typewhisper-128.png"
ICON_TARGET="$ICONS_DIR/typewhisper.png"
BIN_LINK="$BIN_DIR/typewhisper"
EXECUTABLE_PATH="$APP_DIR/typewhisper"
STATE_DIR="$STATE_HOME/typewhisper/installer"

case "$APP_DIR" in
  ""|/|"$HOME"|"$HOME/"|"$DATA_DIR")
    printf "Refusing to install: APP_DIR ('%s') is unsafe.\n" "$APP_DIR" >&2
    exit 1
    ;;
esac

if ! command -v dotnet >/dev/null 2>&1; then
  printf 'dotnet SDK is required to build and install %s.\n' "$APP_NAME" >&2
  exit 1
fi

printf 'Publishing %s (%s, %s)...\n' "$APP_NAME" "$CONFIG" "$RID"
dotnet publish "$PROJECT" \
  -c "$CONFIG" \
  -r "$RID" \
  --self-contained true \
  /p:PublishSingleFile=false \
  /p:DebugType=None \
  /p:DebugSymbols=false \
  --nologo

printf 'Bundling Linux plugins...\n'
bash "$ROOT/scripts/deploy-linux-plugins.sh" "$CONFIG"

mkdir -p "$PUBLISH_DIR/Plugins"
if [ -d "$ROOT/src/TypeWhisper.Linux/bin/$CONFIG/net10.0/Plugins" ]; then
  rm -rf -- "$PUBLISH_DIR/Plugins"
  cp -R "$ROOT/src/TypeWhisper.Linux/bin/$CONFIG/net10.0/Plugins" "$PUBLISH_DIR/Plugins"
fi

# A historical installer wrote published binaries into the user-data directory.
# Report that layout but never sweep it: only a recorded installation manifest
# authorizes removal.
if [ -f "$DATA_DIR/typewhisper" ] || find "$DATA_DIR" -maxdepth 1 -type f -name '*.dll' -print -quit 2>/dev/null | grep -q .; then
  printf 'WARN: %s contains app binaries from the old install layout.\n' "$DATA_DIR" >&2
  printf '      User data is untouched; remove those legacy files manually if desired.\n' >&2
fi

TEMP_DIR="$(mktemp -d "${TMPDIR:-/tmp}/typewhisper-local-install.XXXXXX")"
cleanup_temp() {
  rm -rf -- "$TEMP_DIR"
}
trap cleanup_temp EXIT
DESKTOP_SOURCE="$TEMP_DIR/typewhisper.desktop"
cat >"$DESKTOP_SOURCE" <<DESKTOP
[Desktop Entry]
Type=Application
Version=1.0
Name=$APP_NAME
GenericName=Voice-to-text dictation
Comment=Speech-to-text dictation for Linux desktop
Exec=$EXECUTABLE_PATH
Icon=$ICON_TARGET
Terminal=false
Categories=Utility;Accessibility;AudioVideo;
StartupNotify=true
StartupWMClass=typewhisper
DESKTOP

ma_initialize typewhisper "$STATE_DIR"
ma_register_directory app "$APP_DIR" "$PUBLISH_DIR"
ma_register_file desktop "$DESKTOP_FILE" "$DESKTOP_SOURCE" 0644
ma_register_file icon "$ICON_TARGET" "$ICON_SOURCE" 0644
ma_register_link launcher "$BIN_LINK" "$EXECUTABLE_PATH"

# Installs predating the manifest carry no record, so adopt them on ownership
# evidence rather than refusing every upgrade as foreign.
ma_register_adoption app payload
ma_register_adoption desktop desktop "$APP_DIR"
ma_register_adoption icon icon "$DESKTOP_FILE"
ma_register_adoption launcher link-into "$APP_DIR"

printf 'Installing managed app artifacts...\n'
ma_install

if command -v update-desktop-database >/dev/null 2>&1; then
  update-desktop-database "$APPLICATIONS_DIR" >/dev/null 2>&1 || true
fi
if command -v gtk-update-icon-cache >/dev/null 2>&1; then
  gtk-update-icon-cache "$DATA_HOME/icons/hicolor" >/dev/null 2>&1 || true
fi

printf '\n%s is installed.\n' "$APP_NAME"
printf 'Launcher:   %s\n' "$DESKTOP_FILE"
printf 'Executable: %s\n' "$EXECUTABLE_PATH"
printf 'Data dir:   %s  (untouched — settings, history, models, keys)\n' "$DATA_DIR"
printf 'Start it from your desktop app menu or run: typewhisper\n'
