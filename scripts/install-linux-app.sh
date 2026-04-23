#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
PROJECT="$ROOT/src/TypeWhisper.Linux/TypeWhisper.Linux.csproj"
CONFIG="${CONFIGURATION:-Release}"
RID="${RID:-linux-x64}"
APP_ID="typewhisper"
APP_NAME="TypeWhisper"
PUBLISH_DIR="$ROOT/src/TypeWhisper.Linux/bin/$CONFIG/net10.0/$RID/publish"
INSTALL_ROOT="${XDG_DATA_HOME:-$HOME/.local/share}/TypeWhisper"
APPLICATIONS_DIR="${XDG_DATA_HOME:-$HOME/.local/share}/applications"
ICONS_DIR="${XDG_DATA_HOME:-$HOME/.local/share}/icons/hicolor/128x128/apps"
DESKTOP_FILE="$APPLICATIONS_DIR/$APP_ID.desktop"
ICON_SOURCE="$ROOT/src/TypeWhisper.Linux/Resources/typewhisper-128.png"
ICON_TARGET="$ICONS_DIR/$APP_ID.png"
EXECUTABLE_NAME="typewhisper"
EXECUTABLE_PATH="$INSTALL_ROOT/$EXECUTABLE_NAME"

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

echo "Installing into $INSTALL_ROOT..."
rm -rf "$INSTALL_ROOT"
mkdir -p "$INSTALL_ROOT"
cp -R "$PUBLISH_DIR"/. "$INSTALL_ROOT/"

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
EOF

if command -v update-desktop-database >/dev/null 2>&1; then
  update-desktop-database "$APPLICATIONS_DIR" >/dev/null 2>&1 || true
fi

if command -v gtk-update-icon-cache >/dev/null 2>&1; then
  gtk-update-icon-cache "${XDG_DATA_HOME:-$HOME/.local/share}/icons/hicolor" >/dev/null 2>&1 || true
fi

echo ""
echo "$APP_NAME is installed."
echo "Launcher: $DESKTOP_FILE"
echo "Executable: $EXECUTABLE_PATH"
echo "You should now be able to start it from your desktop app menu."
