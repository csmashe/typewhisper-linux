#!/usr/bin/env bash
# Validate, extract, install, and execute every Linux package produced by
# build-linux-packages.sh. Package-manager mutations happen only in disposable
# containers; host-side work is limited to metadata inspection and extraction.
#
# Usage:
#   scripts/smoke-test-linux-packages.sh <version> [package-dir]
#   scripts/smoke-test-linux-packages.sh <version> [package-dir] --validate-only
#
# --validate-only runs filename, metadata, and extraction checks without Docker.
# It is intended for constrained local development environments, never CI.

set -euo pipefail

fail() {
  echo "ERROR: $*" >&2
  exit 1
}

require_command() {
  command -v "$1" >/dev/null 2>&1 || fail "required command '$1' is not available."
}

require_file() {
  [ -s "$1" ] || fail "required file is missing or empty: $1"
}

require_executable() {
  [ -x "$1" ] || fail "required executable is missing or not executable: $1"
}

assert_removed() {
  local path="$1"
  if [ -e "$path" ] || [ -L "$path" ]; then
    fail "package-owned path remains after removal: $path"
  fi
}

prepare_isolated_profile() {
  SMOKE_HOME_ROOT=/tmp/typewhisper-smoke-home
  SMOKE_DATA_ROOT=/tmp/typewhisper-smoke-data
  SMOKE_CONFIG_ROOT=/tmp/typewhisper-smoke-config
  SMOKE_CACHE_ROOT=/tmp/typewhisper-smoke-cache
  SMOKE_STATE_ROOT=/tmp/typewhisper-smoke-state
  SMOKE_RUNTIME_ROOT=/tmp/typewhisper-smoke-runtime
  SMOKE_PROFILE_ENV=(
    "HOME=$SMOKE_HOME_ROOT"
    "XDG_DATA_HOME=$SMOKE_DATA_ROOT"
    "XDG_CONFIG_HOME=$SMOKE_CONFIG_ROOT"
    "XDG_CACHE_HOME=$SMOKE_CACHE_ROOT"
    "XDG_STATE_HOME=$SMOKE_STATE_ROOT"
    "XDG_RUNTIME_DIR=$SMOKE_RUNTIME_ROOT"
    "TYPEWHISPER_DISABLE_IME=1"
    "LIBGL_ALWAYS_SOFTWARE=1"
  )

  rm -rf \
    "$SMOKE_HOME_ROOT" \
    "$SMOKE_DATA_ROOT" \
    "$SMOKE_CONFIG_ROOT" \
    "$SMOKE_CACHE_ROOT" \
    "$SMOKE_STATE_ROOT" \
    "$SMOKE_RUNTIME_ROOT"
  mkdir -p \
    "$SMOKE_HOME_ROOT" \
    "$SMOKE_DATA_ROOT" \
    "$SMOKE_CONFIG_ROOT" \
    "$SMOKE_CACHE_ROOT" \
    "$SMOKE_STATE_ROOT" \
    "$SMOKE_RUNTIME_ROOT"
  chmod 0700 "$SMOKE_RUNTIME_ROOT"
}

run_help_probe() {
  local executable="$1"
  local output status

  echo "==> Executing --help: $executable"
  set +e
  output=$(
    timeout --signal=TERM --kill-after=5s 30s \
      env "${SMOKE_PROFILE_ENV[@]}" "$executable" --help 2>&1
  )
  status=$?
  set -e
  printf '%s\n' "$output"

  [ "$status" -eq 0 ] || fail "'$executable --help' exited with status $status."
  grep -Fq "Usage:" <<<"$output" \
    || fail "'$executable --help' did not print the expected usage marker."
}

run_cli_probe() {
  local executable="$1"
  local stdout_file=/tmp/typewhisper-cli-stdout
  local stderr_file=/tmp/typewhisper-cli-stderr
  local status actual expected

  echo "==> Executing CLI version probe: $executable"
  set +e
  timeout --signal=TERM --kill-after=5s 30s \
    env "${SMOKE_PROFILE_ENV[@]}" "$executable" --version \
    >"$stdout_file" 2>"$stderr_file"
  status=$?
  set -e
  cat "$stdout_file"
  cat "$stderr_file" >&2

  [ "$status" -eq 0 ] || fail "'$executable --version' exited with status $status."
  # Byte-exact, including the trailing newline. Command substitution strips
  # trailing newlines, so both sides carry an 'x' sentinel to preserve them;
  # a bare comparison would accept a missing or duplicated final newline.
  # Done in-shell rather than with cmp: diffutils is not installed in the
  # Fedora smoke container.
  actual="$(cat "$stdout_file"; printf 'x')"
  expected="$(printf 'typewhisper-cli %s\nx' "$EXPECTED_CLI_VERSION")"
  [ "$actual" = "$expected" ] \
    || fail "'$executable --version' did not print the exact expected version."
  [ ! -s "$stderr_file" ] \
    || fail "'$executable --version' unexpectedly wrote to stderr."

  echo "==> Executing controlled CLI status failure: $executable"
  set +e
  timeout --signal=TERM --kill-after=5s 30s \
    env "${SMOKE_PROFILE_ENV[@]}" "$executable" status \
    >"$stdout_file" 2>"$stderr_file"
  status=$?
  set -e
  cat "$stdout_file"
  cat "$stderr_file" >&2

  # Exact code: a bare "not zero" also accepts timeout kills (124, or 137 once
  # --kill-after has to SIGKILL a CLI that hung after printing the error).
  [ "$status" -eq 1 ] \
    || fail "'$executable status' exited with status $status; expected 1."
  grep -Fq "TypeWhisper API socket not found" "$stderr_file" \
    || fail "'$executable status' did not report the expected missing API socket."
}

run_gui_probe() {
  local executable="$1"
  local display_number=99
  local gui_status xvfb_pid

  echo "==> Starting bounded headless GUI probe: $executable"
  rm -f "/tmp/.X${display_number}-lock"
  rm -rf "/tmp/.X11-unix/X${display_number}"
  Xvfb ":${display_number}" -screen 0 1280x800x24 -nolisten tcp \
    >/tmp/typewhisper-xvfb.log 2>&1 &
  xvfb_pid=$!

  for _ in {1..50}; do
    if [ -S "/tmp/.X11-unix/X${display_number}" ]; then
      break
    fi
    if ! kill -0 "$xvfb_pid" 2>/dev/null; then
      cat /tmp/typewhisper-xvfb.log >&2
      fail "Xvfb exited before the GUI probe started."
    fi
    sleep 0.1
  done

  if [ ! -S "/tmp/.X11-unix/X${display_number}" ]; then
    cat /tmp/typewhisper-xvfb.log >&2
    kill "$xvfb_pid" 2>/dev/null || true
    wait "$xvfb_pid" 2>/dev/null || true
    fail "Xvfb did not become ready."
  fi

  set +e
  timeout --signal=TERM --kill-after=5s 20s \
    env "${SMOKE_PROFILE_ENV[@]}" DISPLAY=":${display_number}" \
    dbus-run-session -- "$executable" --minimized 2>&1 \
    | tee /tmp/typewhisper-gui.log
  gui_status=${PIPESTATUS[0]}
  set -e

  kill "$xvfb_pid" 2>/dev/null || true
  wait "$xvfb_pid" 2>/dev/null || true

  if [ "$gui_status" -ne 124 ]; then
    echo "Xvfb diagnostics:" >&2
    cat /tmp/typewhisper-xvfb.log >&2
    fail "GUI probe exited before its 20-second health window (status $gui_status)."
  fi

  echo "    GUI remained alive for the full 20-second health window."
}

install_ubuntu_runtime() {
  export DEBIAN_FRONTEND=noninteractive
  apt-get update
  # libjack/libasound back the bundled libportaudio.so. A desktop gets them via
  # pipewire-jack; a bare container does not, and without them PortAudio fails to
  # load and the GUI probe can only ever prove the no-audio path.
  apt-get install -y --no-install-recommends \
    dbus-x11 \
    gzip \
    libdbus-1-3 \
    libegl1 \
    libfontconfig1 \
    libfreetype6 \
    libgl1 \
    libice6 \
    libicu74 \
    libjack-jackd2-0 \
    libasound2t64 \
    libsm6 \
    libx11-6 \
    libx11-xcb1 \
    libxcb1 \
    libxcursor1 \
    libxext6 \
    libxfixes3 \
    libxi6 \
    libxkbcommon-x11-0 \
    libxkbcommon0 \
    libxrandr2 \
    libxrender1 \
    tar \
    xvfb
}

install_fedora_runtime() {
  # See install_ubuntu_runtime: alsa-lib/jack back the bundled libportaudio.so.
  dnf install -y \
    alsa-lib \
    dbus-daemon \
    fontconfig \
    freetype \
    gzip \
    jack-audio-connection-kit \
    libICE \
    libSM \
    libX11 \
    libX11-xcb \
    libXcursor \
    libXext \
    libXfixes \
    libXi \
    libXrandr \
    libXrender \
    libglvnd-egl \
    libglvnd-glx \
    libicu \
    libxcb \
    libxkbcommon \
    libxkbcommon-x11 \
    tar \
    xorg-x11-server-Xvfb
}

assert_no_system_dotnet() {
  if command -v dotnet >/dev/null 2>&1; then
    fail "container unexpectedly provides system dotnet at $(command -v dotnet)."
  fi
}

container_smoke_tarball() {
  local package="$1"
  local app_root extracted install_script

  install_ubuntu_runtime
  assert_no_system_dotnet
  prepare_isolated_profile

  extracted=/tmp/typewhisper-tarball
  mkdir -p "$extracted"
  tar -xzf "$package" -C "$extracted"
  install_script=$(find "$extracted" -mindepth 2 -maxdepth 2 -type f -name install.sh -print)
  [ "$(printf '%s\n' "$install_script" | sed '/^$/d' | wc -l)" -eq 1 ] \
    || fail "tarball container extraction did not produce exactly one install.sh."

  mkdir -p "$SMOKE_DATA_ROOT/TypeWhisper"
  printf 'preserve application data\n' >"$SMOKE_DATA_ROOT/TypeWhisper/smoke-sentinel"

  echo "==> Installing tarball into isolated HOME/XDG roots"
  env "${SMOKE_PROFILE_ENV[@]}" bash "$install_script"
  env "${SMOKE_PROFILE_ENV[@]}" bash "$install_script"
  app_root="$SMOKE_DATA_ROOT/typewhisper-app"
  require_executable "$SMOKE_HOME_ROOT/.local/bin/typewhisper"
  require_executable "$app_root/Cli/typewhisper-cli"
  require_file "$SMOKE_DATA_ROOT/applications/typewhisper.desktop"
  require_file "$SMOKE_DATA_ROOT/icons/hicolor/128x128/apps/typewhisper.png"
  [ -d "$app_root" ] \
    || fail "tarball application directory was not installed."
  require_file "$SMOKE_DATA_ROOT/TypeWhisper/smoke-sentinel"
  [ -L "$SMOKE_HOME_ROOT/.local/bin/typewhisper" ] \
    || fail "tarball launcher is not a symlink."
  [ "$(readlink "$SMOKE_HOME_ROOT/.local/bin/typewhisper")" = \
    "$app_root/typewhisper" ] \
    || fail "tarball launcher does not target the installed application."
  grep -Fxq "Exec=$app_root/typewhisper" \
    "$SMOKE_DATA_ROOT/applications/typewhisper.desktop" \
    || fail "installed tarball desktop entry does not target the installed application."

  run_cli_probe "$app_root/Cli/typewhisper-cli"
  run_help_probe "$SMOKE_HOME_ROOT/.local/bin/typewhisper"
  run_gui_probe "$SMOKE_HOME_ROOT/.local/bin/typewhisper"

  echo "==> Uninstalling tarball from isolated HOME/XDG roots"
  env "${SMOKE_PROFILE_ENV[@]}" bash "$install_script" --uninstall
  assert_removed "$SMOKE_HOME_ROOT/.local/bin/typewhisper"
  assert_removed "$SMOKE_DATA_ROOT/applications/typewhisper.desktop"
  assert_removed "$SMOKE_DATA_ROOT/icons/hicolor/128x128/apps/typewhisper.png"
  assert_removed "$app_root"
  require_file "$SMOKE_DATA_ROOT/TypeWhisper/smoke-sentinel"
}

container_smoke_appimage() {
  local package="$1"
  local app_run cli_executable

  install_ubuntu_runtime
  assert_no_system_dotnet
  prepare_isolated_profile

  mkdir -p /tmp/typewhisper-appimage
  echo "==> Extracting AppImage without FUSE in container"
  (
    cd /tmp/typewhisper-appimage
    "$package" --appimage-extract
  )
  app_run=/tmp/typewhisper-appimage/squashfs-root/AppRun
  cli_executable=/tmp/typewhisper-appimage/squashfs-root/usr/bin/Cli/typewhisper-cli
  require_executable "$app_run"
  require_executable "$cli_executable"

  run_cli_probe "$cli_executable"
  run_help_probe "$app_run"
  run_gui_probe "$app_run"
}

container_smoke_deb() {
  local package="$1"
  local owned_path package_files

  install_ubuntu_runtime
  assert_no_system_dotnet
  prepare_isolated_profile

  echo "==> Installing deb in disposable Ubuntu container"
  apt-get install -y --no-install-recommends "$package"
  require_executable /usr/bin/typewhisper
  require_executable /usr/bin/typewhisper-cli
  require_executable /opt/typewhisper/Cli/typewhisper-cli
  require_file /usr/share/applications/typewhisper.desktop
  require_file /usr/share/icons/hicolor/128x128/apps/typewhisper.png
  [ -d /opt/typewhisper ] || fail "deb application directory was not installed."
  package_files=$(dpkg-query --listfiles typewhisper)
  for owned_path in \
    /usr/bin/typewhisper \
    /usr/bin/typewhisper-cli \
    /usr/share/applications/typewhisper.desktop \
    /usr/share/icons/hicolor/128x128/apps/typewhisper.png \
    /opt/typewhisper/Cli/typewhisper-cli \
    /opt/typewhisper; do
    grep -Fxq "$owned_path" <<<"$package_files" \
      || fail "deb database does not own expected path: $owned_path"
  done

  run_cli_probe /opt/typewhisper/Cli/typewhisper-cli
  run_cli_probe /usr/bin/typewhisper-cli
  run_help_probe /usr/bin/typewhisper
  run_gui_probe /usr/bin/typewhisper

  echo "==> Removing deb"
  apt-get remove -y typewhisper
  assert_removed /usr/bin/typewhisper
  assert_removed /usr/bin/typewhisper-cli
  assert_removed /usr/share/applications/typewhisper.desktop
  assert_removed /usr/share/icons/hicolor/128x128/apps/typewhisper.png
  assert_removed /opt/typewhisper/Cli/typewhisper-cli
  assert_removed /opt/typewhisper
}

container_smoke_rpm() {
  local package="$1"
  local owned_path package_files

  install_fedora_runtime
  assert_no_system_dotnet
  prepare_isolated_profile

  echo "==> Installing rpm in disposable Fedora container"
  dnf install -y "$package"
  require_executable /usr/bin/typewhisper
  require_executable /usr/bin/typewhisper-cli
  require_executable /opt/typewhisper/Cli/typewhisper-cli
  require_file /usr/share/applications/typewhisper.desktop
  require_file /usr/share/icons/hicolor/128x128/apps/typewhisper.png
  [ -d /opt/typewhisper ] || fail "rpm application directory was not installed."
  package_files=$(rpm -ql typewhisper)
  for owned_path in \
    /usr/bin/typewhisper \
    /usr/bin/typewhisper-cli \
    /usr/share/applications/typewhisper.desktop \
    /usr/share/icons/hicolor/128x128/apps/typewhisper.png \
    /opt/typewhisper/Cli/typewhisper-cli \
    /opt/typewhisper; do
    grep -Fxq "$owned_path" <<<"$package_files" \
      || fail "rpm database does not own expected path: $owned_path"
  done

  run_cli_probe /opt/typewhisper/Cli/typewhisper-cli
  run_cli_probe /usr/bin/typewhisper-cli
  run_help_probe /usr/bin/typewhisper
  run_gui_probe /usr/bin/typewhisper

  echo "==> Removing rpm"
  dnf remove -y typewhisper
  assert_removed /usr/bin/typewhisper
  assert_removed /usr/bin/typewhisper-cli
  assert_removed /usr/share/applications/typewhisper.desktop
  assert_removed /usr/share/icons/hicolor/128x128/apps/typewhisper.png
  assert_removed /opt/typewhisper/Cli/typewhisper-cli
  assert_removed /opt/typewhisper
}

if [ "${1:-}" = "--container" ]; then
  [ "$#" -eq 4 ] \
    || fail "internal container mode requires a format, package path, and CLI version."
  EXPECTED_CLI_VERSION="$4"
  case "$2" in
    tarball) container_smoke_tarball "$3" ;;
    appimage) container_smoke_appimage "$3" ;;
    deb) container_smoke_deb "$3" ;;
    rpm) container_smoke_rpm "$3" ;;
    *) fail "unknown internal container format '$2'." ;;
  esac
  echo "==> Container smoke passed: $2"
  exit 0
fi

VERSION="${1:-}"
PACKAGE_DIR="${2:-dist}"
MODE="${3:-}"

if [ -z "$VERSION" ]; then
  echo "Usage: $0 <version> [package-dir] [--validate-only]" >&2
  exit 2
fi
if [[ ! "$VERSION" =~ ^v?[0-9]+\.[0-9]+\.[0-9]+(-[0-9A-Za-z.-]+)?(\+[0-9A-Za-z.-]+)?$ ]]; then
  fail "expected version '$VERSION' is not a supported SemVer value."
fi
if [ -n "$MODE" ] && [ "$MODE" != "--validate-only" ]; then
  fail "unknown mode '$MODE'; expected --validate-only."
fi
if [ "$MODE" = "--validate-only" ] && [ "${CI:-}" = "true" ]; then
  fail "--validate-only is disabled in CI; container smoke tests are mandatory."
fi
if [ "$#" -gt 3 ]; then
  fail "too many arguments."
fi

for command in cpio dpkg-deb file find grep rpm rpm2cpio sed tar timeout; do
  require_command "$command"
done

[ -d "$PACKAGE_DIR" ] || fail "package directory does not exist: $PACKAGE_DIR"
PACKAGE_DIR="$(cd "$PACKAGE_DIR" && pwd)"
SCRIPT_PATH="$(readlink -f "${BASH_SOURCE[0]}")"
EXPECTED_VERSION="${VERSION#v}"
EXPECTED_CLI_VERSION="${EXPECTED_VERSION%%+*}"
RPM_VERSION="${EXPECTED_VERSION//-/\~}"

EXPECTED_TARBALL="$PACKAGE_DIR/typewhisper-linux-x64-${VERSION}.tar.gz"
EXPECTED_APPIMAGE="$PACKAGE_DIR/TypeWhisper-${VERSION}-x86_64.AppImage"
EXPECTED_DEB="$PACKAGE_DIR/typewhisper_${EXPECTED_VERSION}_amd64.deb"
EXPECTED_RPM="$PACKAGE_DIR/typewhisper-${RPM_VERSION}-1.x86_64.rpm"

shopt -s nullglob
tarballs=("$PACKAGE_DIR"/*.tar.gz)
appimages=("$PACKAGE_DIR"/*.AppImage)
debs=("$PACKAGE_DIR"/*.deb)
rpms=("$PACKAGE_DIR"/*.rpm)
shopt -u nullglob

check_exact_artifact() {
  local format="$1"
  local expected="$2"
  shift 2
  local matches=("$@")

  [ "${#matches[@]}" -eq 1 ] \
    || fail "expected exactly one $format in '$PACKAGE_DIR', found ${#matches[@]}."
  [ "${matches[0]}" = "$expected" ] \
    || fail "$format filename mismatch: expected '$(basename "$expected")', found '$(basename "${matches[0]}")'."
  require_file "$expected"
}

check_exact_artifact "tarball" "$EXPECTED_TARBALL" "${tarballs[@]}"
check_exact_artifact "AppImage" "$EXPECTED_APPIMAGE" "${appimages[@]}"
check_exact_artifact "deb" "$EXPECTED_DEB" "${debs[@]}"
check_exact_artifact "rpm" "$EXPECTED_RPM" "${rpms[@]}"
require_executable "$EXPECTED_APPIMAGE"

echo "==> Debian metadata"
dpkg-deb --info "$EXPECTED_DEB"
[ "$(dpkg-deb -f "$EXPECTED_DEB" Package)" = "typewhisper" ] \
  || fail "deb Package metadata is not 'typewhisper'."
[ "$(dpkg-deb -f "$EXPECTED_DEB" Version)" = "$EXPECTED_VERSION" ] \
  || fail "deb Version metadata does not match '$EXPECTED_VERSION'."
[ "$(dpkg-deb -f "$EXPECTED_DEB" Architecture)" = "amd64" ] \
  || fail "deb Architecture metadata is not 'amd64'."

echo "==> RPM metadata"
rpm -qip "$EXPECTED_RPM"
[ "$(rpm -qp --queryformat '%{NAME}' "$EXPECTED_RPM")" = "typewhisper" ] \
  || fail "rpm Name metadata is not 'typewhisper'."
[ "$(rpm -qp --queryformat '%{VERSION}' "$EXPECTED_RPM")" = "$RPM_VERSION" ] \
  || fail "rpm Version metadata does not match '$RPM_VERSION'."
[ "$(rpm -qp --queryformat '%{RELEASE}' "$EXPECTED_RPM")" = "1" ] \
  || fail "rpm Release metadata is not '1'."
[ "$(rpm -qp --queryformat '%{ARCH}' "$EXPECTED_RPM")" = "x86_64" ] \
  || fail "rpm Architecture metadata is not 'x86_64'."

EXTRACT_ROOT="$(mktemp -d)"
cleanup_host() {
  rm -rf "$EXTRACT_ROOT"
}
trap cleanup_host EXIT

TARBALL_EXTRACT="$EXTRACT_ROOT/tarball"
APPIMAGE_EXTRACT="$EXTRACT_ROOT/appimage"
DEB_EXTRACT="$EXTRACT_ROOT/deb"
RPM_EXTRACT="$EXTRACT_ROOT/rpm"
mkdir -p "$TARBALL_EXTRACT" "$APPIMAGE_EXTRACT" "$DEB_EXTRACT" "$RPM_EXTRACT"

echo "==> Extracting every package format on the runner"
tar -xzf "$EXPECTED_TARBALL" --no-same-owner -C "$TARBALL_EXTRACT"
dpkg-deb --extract "$EXPECTED_DEB" "$DEB_EXTRACT"
(
  cd "$RPM_EXTRACT"
  rpm2cpio "$EXPECTED_RPM" | cpio --quiet -idmu --no-absolute-filenames
)
if ! (
  cd "$APPIMAGE_EXTRACT"
  "$EXPECTED_APPIMAGE" --appimage-extract >appimage-extract.log 2>&1
); then
  cat "$APPIMAGE_EXTRACT/appimage-extract.log" >&2
  fail "AppImage extraction without FUSE failed."
fi

validate_desktop_entry() {
  local desktop_file="$1"

  require_file "$desktop_file"
  grep -Fxq "Type=Application" "$desktop_file" \
    || fail "desktop entry has no Type=Application: $desktop_file"
  grep -Fxq "Exec=typewhisper" "$desktop_file" \
    || fail "desktop entry does not launch the shipped app: $desktop_file"

  if command -v desktop-file-validate >/dev/null 2>&1; then
    desktop-file-validate "$desktop_file"
  else
    echo "WARN: desktop-file-validate is unavailable; basic desktop-entry checks passed." >&2
  fi
}

validate_payload() {
  local format="$1"
  local app_dir="$2"
  local executable="$3"
  local desktop_file="$4"
  local icon_file="$5"
  local assembly_name cli_executable native_file plugin_id
  local plugin_dir
  local plugin_dirs=()

  echo "==> Validating extracted $format payload"
  require_executable "$executable"
  file "$executable" | grep -Eq 'ELF 64-bit.*x86-64' \
    || fail "$format app executable is not an x86-64 ELF binary."
  cli_executable="$app_dir/Cli/typewhisper-cli"
  require_executable "$cli_executable"
  file "$cli_executable" | grep -Eq 'ELF 64-bit.*x86-64' \
    || fail "$format CLI executable is not an x86-64 ELF binary."
  validate_desktop_entry "$desktop_file"
  require_file "$icon_file"
  file "$icon_file" | grep -Fq "PNG image data" \
    || fail "$format icon is not a PNG image: $icon_file"

  require_file "$app_dir/typewhisper.dll"
  require_file "$app_dir/typewhisper.deps.json"
  require_file "$app_dir/typewhisper.runtimeconfig.json"
  require_file "$app_dir/TypeWhisper.PluginSDK.dll"
  require_file "$app_dir/libhostfxr.so"
  require_file "$app_dir/libhostpolicy.so"
  require_file "$app_dir/libcoreclr.so"
  require_file "$app_dir/libSkiaSharp.so"
  grep -Fq "\"typewhisper/$EXPECTED_VERSION\":" "$app_dir/typewhisper.deps.json" \
    || fail "$format deployed payload is not stamped with version '$EXPECTED_VERSION'."

  [ -d "$app_dir/Plugins" ] || fail "$format payload has no bundled Plugins directory."
  shopt -s nullglob
  plugin_dirs=("$app_dir"/Plugins/*)
  shopt -u nullglob
  [ "${#plugin_dirs[@]}" -gt 0 ] || fail "$format payload has no bundled plugins."

  for plugin_dir in "${plugin_dirs[@]}"; do
    [ -d "$plugin_dir" ] || fail "unexpected non-directory in bundled Plugins: $plugin_dir"
    require_file "$plugin_dir/manifest.json"
    plugin_id=$(
      sed -nE 's/^[[:space:]]*"id"[[:space:]]*:[[:space:]]*"([^"]+)".*/\1/p' \
        "$plugin_dir/manifest.json"
    )
    [ "$plugin_id" = "$(basename "$plugin_dir")" ] \
      || fail "plugin manifest id does not match its directory: $plugin_dir"
    assembly_name=$(
      sed -nE 's/^[[:space:]]*"assemblyName"[[:space:]]*:[[:space:]]*"([^"]+)".*/\1/p' \
        "$plugin_dir/manifest.json"
    )
    [ -n "$assembly_name" ] \
      || fail "plugin manifest has no assemblyName: $plugin_dir/manifest.json"
    require_file "$plugin_dir/$assembly_name"
  done

  native_file=$(find "$app_dir" -type f \( -name '*.so' -o -name '*.so.*' \) -print -quit)
  [ -n "$native_file" ] || fail "$format payload has no native runtime libraries."
}

TARBALL_APP_DIR="$TARBALL_EXTRACT/typewhisper-linux-x64-${VERSION}"
[ -d "$TARBALL_APP_DIR" ] || fail "tarball did not contain its expected top-level directory."
require_executable "$TARBALL_APP_DIR/install.sh"
validate_payload \
  "tarball" \
  "$TARBALL_APP_DIR" \
  "$TARBALL_APP_DIR/typewhisper" \
  "$TARBALL_APP_DIR/typewhisper.desktop" \
  "$TARBALL_APP_DIR/typewhisper.png"

APPIMAGE_ROOT="$APPIMAGE_EXTRACT/squashfs-root"
require_executable "$APPIMAGE_ROOT/AppRun"
validate_desktop_entry "$APPIMAGE_ROOT/typewhisper.desktop"
require_file "$APPIMAGE_ROOT/typewhisper.png"
file "$APPIMAGE_ROOT/typewhisper.png" | grep -Fq "PNG image data" \
  || fail "AppImage root icon is not a PNG image."
validate_payload \
  "AppImage" \
  "$APPIMAGE_ROOT/usr/bin" \
  "$APPIMAGE_ROOT/usr/bin/typewhisper" \
  "$APPIMAGE_ROOT/usr/share/applications/typewhisper.desktop" \
  "$APPIMAGE_ROOT/usr/share/icons/hicolor/128x128/apps/typewhisper.png"

require_executable "$DEB_EXTRACT/usr/bin/typewhisper"
require_executable "$DEB_EXTRACT/usr/bin/typewhisper-cli"
validate_payload \
  "deb" \
  "$DEB_EXTRACT/opt/typewhisper" \
  "$DEB_EXTRACT/opt/typewhisper/typewhisper" \
  "$DEB_EXTRACT/usr/share/applications/typewhisper.desktop" \
  "$DEB_EXTRACT/usr/share/icons/hicolor/128x128/apps/typewhisper.png"

require_executable "$RPM_EXTRACT/usr/bin/typewhisper"
require_executable "$RPM_EXTRACT/usr/bin/typewhisper-cli"
validate_payload \
  "rpm" \
  "$RPM_EXTRACT/opt/typewhisper" \
  "$RPM_EXTRACT/opt/typewhisper/typewhisper" \
  "$RPM_EXTRACT/usr/share/applications/typewhisper.desktop" \
  "$RPM_EXTRACT/usr/share/icons/hicolor/128x128/apps/typewhisper.png"

echo "==> All host-side metadata and extraction checks passed."
if [ "$MODE" = "--validate-only" ]; then
  echo "==> Container install/execution checks explicitly skipped by --validate-only."
  exit 0
fi

require_command docker
if ! docker info >/dev/null 2>&1; then
  fail "Docker is installed but its daemon is unavailable; container package smoke tests cannot run."
fi

UBUNTU_IMAGE="ubuntu:24.04"
FEDORA_IMAGE="fedora:43"

run_container_smoke() {
  local format="$1"
  local image="$2"
  local package="$3"
  local container_name="typewhisper-package-smoke-${format}-$$"
  local status

  echo "==> Running $format smoke test in pinned image $image"
  set +e
  timeout --signal=INT --kill-after=30s 10m \
    docker run --name "$container_name" --rm --pull=always \
    --mount "type=bind,src=$SCRIPT_PATH,dst=/smoke-test-linux-packages.sh,readonly" \
    --mount "type=bind,src=$PACKAGE_DIR,dst=/packages,readonly" \
    "$image" \
    bash /smoke-test-linux-packages.sh \
    --container "$format" "/packages/$(basename "$package")" "$EXPECTED_CLI_VERSION"
  status=$?
  set -e

  if [ "$status" -ne 0 ]; then
    docker rm --force "$container_name" >/dev/null 2>&1 || true
    fail "$format container smoke test failed or timed out (status $status)."
  fi
}

# Keep these sequential so logs identify the failing format and package-manager
# operations never overlap on the runner.
run_container_smoke "tarball" "$UBUNTU_IMAGE" "$EXPECTED_TARBALL"
run_container_smoke "appimage" "$UBUNTU_IMAGE" "$EXPECTED_APPIMAGE"
run_container_smoke "deb" "$UBUNTU_IMAGE" "$EXPECTED_DEB"
run_container_smoke "rpm" "$FEDORA_IMAGE" "$EXPECTED_RPM"

echo "==> All Linux package smoke tests passed."
