#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
PROJECT="$ROOT/src/TypeWhisper.Linux/TypeWhisper.Linux.csproj"
CONFIG="${CONFIGURATION:-Release}"
RID="${RID:-linux-x64}"
APP_ID="typewhisper"
APP_NAME="TypeWhisper"
PUBLISH_DIR="$ROOT/src/TypeWhisper.Linux/bin/$CONFIG/net10.0/$RID/publish"
# Where the app BINARY goes. This MUST stay separate from the app's runtime
# data directory (TypeWhisperEnvironment.BasePath = $XDG_DATA_HOME/TypeWhisper),
# which holds settings.json, history, downloaded models (multi-GB), and
# per-plugin API keys. An earlier version of this script installed the binary
# INTO that data dir and ran `rm -rf` on it every run, destroying all user data.
# Keep these two paths distinct; this script must never delete the data dir.
DATA_DIR="${XDG_DATA_HOME:-$HOME/.local/share}/TypeWhisper"
APP_DIR="${XDG_DATA_HOME:-$HOME/.local/share}/typewhisper-app"
APPLICATIONS_DIR="${XDG_DATA_HOME:-$HOME/.local/share}/applications"
ICONS_DIR="${XDG_DATA_HOME:-$HOME/.local/share}/icons/hicolor/128x128/apps"
DESKTOP_FILE="$APPLICATIONS_DIR/$APP_ID.desktop"
ICON_SOURCE="$ROOT/src/TypeWhisper.Linux/Resources/typewhisper-128.png"
ICON_TARGET="$ICONS_DIR/$APP_ID.png"
EXECUTABLE_NAME="typewhisper"
EXECUTABLE_PATH="$APP_DIR/$EXECUTABLE_NAME"

[ -n "${HOME:-}" ] || { echo "HOME must be set." >&2; exit 1; }

if ! command -v dotnet >/dev/null 2>&1; then
  echo "dotnet SDK is required to build and install $APP_NAME." >&2
  exit 1
fi

echo "Publishing $APP_NAME ($CONFIG, $RID)..."
dotnet publish "$PROJECT" \
  -c "$CONFIG" \
  -r "$RID" \
  --self-contained true \
  /p:PublishSingleFile=false \
  /p:DebugType=None \
  /p:DebugSymbols=false \
  --nologo

echo "Bundling Linux plugins..."
bash "$ROOT/scripts/deploy-linux-plugins.sh" "$CONFIG"

mkdir -p "$PUBLISH_DIR/Plugins"
if [ -d "$ROOT/src/TypeWhisper.Linux/bin/$CONFIG/net10.0/Plugins" ]; then
  rm -rf "$PUBLISH_DIR/Plugins"
  cp -R "$ROOT/src/TypeWhisper.Linux/bin/$CONFIG/net10.0/Plugins" "$PUBLISH_DIR/Plugins"
fi

# Belt-and-suspenders: refuse to wipe a path that could be user data, even if a
# future edit mis-sets APP_DIR. The data dir, HOME, and empty strings are out.
case "$APP_DIR" in
  "$DATA_DIR"|"$HOME"|""|"/"|"$HOME/")
    echo "Refusing to install: APP_DIR ('$APP_DIR') is unsafe to remove." >&2
    exit 1
    ;;
esac

# Heads-up if a previous (buggy) install left the binary payload inside the data
# dir. Don't auto-delete — just tell the user; their data is intact.
if [ -f "$DATA_DIR/$EXECUTABLE_NAME" ] || ls "$DATA_DIR"/*.dll >/dev/null 2>&1; then
  echo "WARN: $DATA_DIR contains app binaries from the old install layout." >&2
  echo "      Your settings/history/keys are safe. You may remove the stray" >&2
  echo "      *.dll/*.so files and the '$EXECUTABLE_NAME' binary from there by hand;" >&2
  echo "      this script no longer writes the binary into the data dir." >&2
fi

echo "Installing app into $APP_DIR..."
# Safe to wipe: APP_DIR holds only the published binary payload, never user data.
rm -rf "$APP_DIR"
mkdir -p "$APP_DIR"
cp -R "$PUBLISH_DIR"/. "$APP_DIR/"

mkdir -p "$APPLICATIONS_DIR" "$ICONS_DIR"
cp "$ICON_SOURCE" "$ICON_TARGET"
chmod +x "$EXECUTABLE_PATH"

cat > "$DESKTOP_FILE" <<EOF
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
EOF

if command -v update-desktop-database >/dev/null 2>&1; then
  update-desktop-database "$APPLICATIONS_DIR" >/dev/null 2>&1 || true
fi

if command -v gtk-update-icon-cache >/dev/null 2>&1; then
  gtk-update-icon-cache "${XDG_DATA_HOME:-$HOME/.local/share}/icons/hicolor" >/dev/null 2>&1 || true
fi

echo ""
echo "$APP_NAME is installed."
echo "Launcher:   $DESKTOP_FILE"
echo "Executable: $EXECUTABLE_PATH"
echo "Data dir:   $DATA_DIR  (untouched — settings, history, models, keys)"
echo "You should now be able to start it from your desktop app menu."
