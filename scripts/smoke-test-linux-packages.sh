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

BUNDLE_IDENTITY_FILE_NAME=".typewhisper-bundle-identity.sha256"

compute_bundle_identity() {
  local plugin_root="$1"
  local file_digest relative_path

  (
    cd "$plugin_root"
    while IFS= read -r -d '' relative_path; do
      file_digest="$(sha256sum <"$relative_path")"
      file_digest="${file_digest%% *}"
      printf '%s\0%s\n' "${relative_path#./}" "$file_digest"
    done < <(
      find . -type f \
        ! -path "./$BUNDLE_IDENTITY_FILE_NAME" \
        -print0 \
        | LC_ALL=C sort -z
    )
  ) | sha256sum | cut -d ' ' -f1
}

compute_glibc_floor() {
  local payload_root="$1"
  local candidate candidate_list verneed verneeds floors floor

  # Enumerate through a checked find first: process substitution would hide a
  # partial enumeration failure and silently under-floor the packages.
  candidate_list="$(mktemp)"
  if ! find "$payload_root" \( -type f -o -type l \) -print0 >"$candidate_list"; then
    rm -f "$candidate_list"
    echo "ERROR: failed to enumerate payload files under $payload_root" >&2
    return 1
  fi

  # One "GLIBC_<name> <path>" line per distinct GLIBC_* verneed per ELF, so an
  # unrecognized name can be reported with the binary that requires it. A
  # failing readelf -V is fatal: tolerating it would drop that ELF's verneeds
  # and silently under-floor the packages. grep exit 1 is tolerated — it
  # just means the ELF has no GLIBC verneeds (e.g. a static binary).
  if ! verneeds="$(
    while IFS= read -r -d '' candidate; do
      if readelf -h "$candidate" >/dev/null 2>&1; then
        if ! version_info="$(readelf -V "$candidate" 2>/dev/null)"; then
          echo "ERROR: readelf -V failed for $candidate" >&2
          exit 1
        fi
        grep_status=0
        candidate_verneeds="$(grep -oE 'GLIBC_[0-9A-Za-z_.]+' <<<"$version_info")" \
          || grep_status=$?
        if [ "$grep_status" -gt 1 ]; then
          echo "ERROR: scanning GLIBC verneeds failed for $candidate" >&2
          exit 1
        fi
        if [ -n "$candidate_verneeds" ]; then
          LC_ALL=C sort -u <<<"$candidate_verneeds" \
            | while IFS= read -r verneed; do
                printf '%s %s\n' "$verneed" "$candidate"
              done
        fi
      fi
    done <"$candidate_list"
  )"; then
    rm -f "$candidate_list"
    return 1
  fi
  rm -f "$candidate_list"

  # Verneed names are not all numeric versions, and numeric ones may carry
  # three components (x86-64's glibc baseline is GLIBC_2.2.5). GLIBC_ABI_DT_RELR
  # marks packed DT_RELR relocations, which glibc first loads at 2.36, so it
  # competes as a 2.36 floor candidate. Any other name (GLIBC_PRIVATE included)
  # has no known version mapping and must fail here rather than silently
  # under-floor the packages.
  floors=""
  while IFS=' ' read -r verneed candidate; do
    [ -n "$verneed" ] || continue
    if [[ "$verneed" =~ ^GLIBC_2\.[0-9]+(\.[0-9]+)?$ ]]; then
      floors+="${verneed#GLIBC_}"$'\n'
    elif [ "$verneed" = "GLIBC_ABI_DT_RELR" ]; then
      floors+="2.36"$'\n'
    else
      echo "ERROR: unrecognized GLIBC verneed '$verneed' required by $candidate; map it to a glibc version floor before shipping" >&2
      return 1
    fi
  done <<<"$verneeds"

  floor="$(printf '%s' "$floors" | LC_ALL=C sort -Vu | tail -n 1)"
  [[ "$floor" =~ ^2\.[0-9]+(\.[0-9]+)?$ ]] \
    || { echo "ERROR: could not determine the staged payload's GLIBC floor" >&2; return 1; }
  printf '%s\n' "$floor"
}

validate_bundle_identity() {
  local format="$1"
  local plugin_root="$2"
  local marker="$plugin_root/$BUNDLE_IDENTITY_FILE_NAME"
  local published recomputed

  require_file "$marker"
  published="$(<"$marker")"
  [[ "$published" =~ ^[[:xdigit:]]{64}$ ]] \
    || fail "$format bundled plugin identity is malformed."
  # Recompute over the extracted payload with only the marker itself excluded.
  recomputed="$(compute_bundle_identity "$plugin_root")"
  [ "${published,,}" = "$recomputed" ] \
    || fail "$format bundled plugin identity does not match its payload."
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

# Written by --install-runtime once the dependencies are baked into the smoke
# image, so the per-format containers can skip the package manager entirely.
RUNTIME_READY_MARKER=/var/lib/typewhisper-smoke/runtime-ready

runtime_already_installed() {
  if [ -f "$RUNTIME_READY_MARKER" ]; then
    echo "==> Runtime dependencies preinstalled in the smoke image."
    return 0
  fi
  return 1
}

mark_runtime_installed() {
  mkdir -p "$(dirname "$RUNTIME_READY_MARKER")"
  printf 'typewhisper smoke runtime dependencies installed\n' >"$RUNTIME_READY_MARKER"
}

# A flaky or throttled mirror otherwise fails the whole smoke run on a single
# dropped connection.
configure_apt_retries() {
  printf 'Acquire::Retries "3";\n' >/etc/apt/apt.conf.d/99-typewhisper-smoke-retries
}

install_ubuntu_runtime() {
  runtime_already_installed && return 0
  export DEBIAN_FRONTEND=noninteractive
  configure_apt_retries
  apt-get update
  # Tarballs and AppImages have no package resolver, so install the full hard
  # closure plus the weak desktop integrations. Probe-only tooling (Xvfb,
  # dbus-run-session) comes from install_ubuntu_probe_infrastructure instead.
  apt-get install -y --no-install-recommends \
    ca-certificates \
    dbus-x11 \
    gzip \
    libdbus-1-3 \
    libegl1 \
    libfontconfig1 \
    libfreetype6 \
    libc6 \
    libgcc-s1 \
    libgl1 \
    libgomp1 \
    libgssapi-krb5-2 \
    libice6 \
    libicu74 \
    libjack-jackd2-0 \
    libasound2t64 \
    libsm6 \
    libssl3t64 \
    libstdc++6 \
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
    libxinerama1 \
    libxt6t64 \
    libxtst6 \
    tar \
    tzdata \
    zlib1g
}

install_ubuntu_probe_infrastructure() {
  apt-get install -y --no-install-recommends dbus-daemon xvfb
}

install_fedora_probe_infrastructure() {
  # retries=3: a flaky mirror must not fail the smoke run on a dropped connection.
  dnf install -y --setopt=retries=3 \
    dbus-daemon \
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
  install_ubuntu_probe_infrastructure
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
  app_root="$SMOKE_DATA_ROOT/typewhisper-app"

  # A second run must upgrade the recorded install in place, not refuse it as foreign.
  env "${SMOKE_PROFILE_ENV[@]}" bash "$install_script"
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
  # The install root carries program payload only: recordings, history, models, keys
  # and settings live in $XDG_DATA_HOME/TypeWhisper, which a plain --uninstall keeps.
  assert_removed "$app_root"
  require_file "$SMOKE_DATA_ROOT/TypeWhisper/smoke-sentinel"

  # --purge is the path that must leave nothing behind, user data included.
  echo "==> Purging tarball install from isolated HOME/XDG roots"
  env "${SMOKE_PROFILE_ENV[@]}" bash "$install_script" --uninstall --purge
  assert_removed "$app_root"
  assert_removed "$SMOKE_DATA_ROOT/TypeWhisper"
}

container_smoke_appimage() {
  local package="$1"
  local app_run cli_executable

  install_ubuntu_runtime
  install_ubuntu_probe_infrastructure
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

  echo "==> Installing deb in disposable Ubuntu container"
  export DEBIAN_FRONTEND=noninteractive
  configure_apt_retries
  apt-get update
  apt-get install -y --no-install-recommends "$package"
  ! dpkg -s libgl1 >/dev/null 2>&1 \
    || fail "deb package transaction unexpectedly installed Recommends-demoted libgl1."
  ! dpkg -s libdbus-1-3 >/dev/null 2>&1 \
    || fail "deb package transaction unexpectedly installed Recommends-demoted libdbus-1-3."
  # The package transaction must resolve on Depends alone. The probe harness is
  # added afterward and may bring its own libraries, so probes do not prove the
  # app runs without the Recommends-demoted libraries.
  install_ubuntu_probe_infrastructure
  assert_no_system_dotnet
  prepare_isolated_profile

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

  echo "==> Installing rpm in disposable Fedora container"
  dnf install -y --setopt=retries=3 --setopt=install_weak_deps=False "$package"
  # The package transaction keeps weak dependencies disabled; the probe harness
  # is added afterward and may supply libraries used by the execution probes.
  install_fedora_probe_infrastructure
  assert_no_system_dotnet
  prepare_isolated_profile

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

# Runs inside `docker build`, so the dependency download happens once per smoke
# run instead of once per package format.
if [ "${1:-}" = "--install-runtime" ]; then
  [ "$#" -eq 2 ] || fail "internal runtime mode requires a distribution."
  case "$2" in
    ubuntu)
      install_ubuntu_runtime
      install_ubuntu_probe_infrastructure
      ;;
    fedora)
      # Probe tooling only: the RPM's declared dependencies must still resolve
      # inside the container, or the smoke run stops validating that metadata.
      install_fedora_probe_infrastructure
      ;;
    *) fail "unknown internal runtime distribution '$2'." ;;
  esac
  mark_runtime_installed
  echo "==> Smoke runtime dependencies installed for $2."
  exit 0
fi

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

for command in cpio dpkg-deb file find grep readelf rpm rpm2cpio sed sha256sum sort tar timeout; do
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

DEB_EXPECTED_DEPENDENCY_GROUPS=(
  "libgcc-s1"
  "libstdc++6"
  "libicu78 | libicu76 | libicu74 | libicu72 | libicu70"
  "libfontconfig1"
  "libx11-6"
  "libxcursor1"
  "libxext6"
  "libxi6"
  "libxrandr2"
  "libice6"
  "libsm6"
  "libxtst6"
  "libxt6t64 | libxt6"
  "libxinerama1"
  "libssl3t64 | libssl3"
  "zlib1g"
  "libgomp1"
  "libgssapi-krb5-2"
  "ca-certificates"
  "tzdata"
  "libasound2t64 | libasound2"
  "libjack-jackd2-0 | libjack0 | pipewire-jack"
)

RPM_EXPECTED_REQUIREMENTS=(
  "/bin/sh"
  "libc.so.6()(64bit)"
  "libgcc_s.so.1()(64bit)"
  "libstdc++.so.6()(64bit)"
  "libicu"
  "libfontconfig.so.1()(64bit)"
  "libX11.so.6()(64bit)"
  "libXcursor.so.1()(64bit)"
  "libXext.so.6()(64bit)"
  "libXi.so.6()(64bit)"
  "libXrandr.so.2()(64bit)"
  "libICE.so.6()(64bit)"
  "libSM.so.6()(64bit)"
  "libXtst.so.6()(64bit)"
  "libXt.so.6()(64bit)"
  "libXinerama.so.1()(64bit)"
  "libssl.so.3()(64bit)"
  "libcrypto.so.3()(64bit)"
  "libz.so.1()(64bit)"
  "libgomp.so.1()(64bit)"
  "libgssapi_krb5.so.2()(64bit)"
  "ca-certificates"
  "tzdata"
  "libjack.so.0()(64bit)"
  "libasound.so.2()(64bit)"
)

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

array_contains_exact() {
  local expected="$1"
  shift
  local actual

  for actual in "$@"; do
    [ "$actual" = "$expected" ] && return 0
  done
  return 1
}

normalize_deb_dependency_groups() {
  local dependency_field="$1"
  local group alternative normalized
  local groups alternatives

  dependency_field="${dependency_field//$'\n'/ }"
  IFS=',' read -ra groups <<<"$dependency_field"
  for group in "${groups[@]}"; do
    normalized=""
    # Appending then removing a marker preserves an otherwise-discarded empty
    # alternative after a trailing '|'.
    IFS='|' read -ra alternatives <<<"${group}|<DEPENDENCY-GROUP-END>"
    unset 'alternatives[${#alternatives[@]}-1]'
    for alternative in "${alternatives[@]}"; do
      alternative="${alternative#"${alternative%%[![:space:]]*}"}"
      alternative="${alternative%"${alternative##*[![:space:]]}"}"
      if [ -z "$alternative" ]; then
        printf '%s\n' "<EMPTY-ALTERNATIVE>"
        continue
      fi
      if [ -n "$normalized" ]; then
        normalized+=" | "
      fi
      normalized+="$alternative"
    done
    printf '%s\n' "$normalized"
  done
}

assert_exact_deb_dependencies() {
  local dependency_field expected_sorted actual_sorted

  dependency_field="$(dpkg-deb -f "$EXPECTED_DEB" Depends)"
  mapfile -t DEB_ACTUAL_DEPENDENCY_GROUPS < <(
    normalize_deb_dependency_groups "$dependency_field"
  )
  expected_sorted="$(
    printf '%s\n' "${DEB_EXPECTED_DEPENDENCY_GROUPS[@]}" | LC_ALL=C sort
  )"
  actual_sorted="$(
    printf '%s\n' "${DEB_ACTUAL_DEPENDENCY_GROUPS[@]}" | LC_ALL=C sort
  )"

  if [ "$actual_sorted" != "$expected_sorted" ]; then
    echo "Expected deb Depends groups:" >&2
    printf '  %s\n' "${DEB_EXPECTED_DEPENDENCY_GROUPS[@]}" >&2
    echo "Actual deb Depends groups:" >&2
    printf '  %s\n' "${DEB_ACTUAL_DEPENDENCY_GROUPS[@]}" >&2
    fail "deb Depends does not exactly match the required dependency groups."
  fi
}

assert_rpm_requirements() {
  local expected_sorted actual_sorted

  mapfile -t RPM_ACTUAL_REQUIREMENTS < <(
    rpm -qp --requires "$EXPECTED_RPM" | grep -vE '^rpmlib\(' || true
  )
  expected_sorted="$(
    printf '%s\n' "${RPM_EXPECTED_REQUIREMENTS[@]}" | LC_ALL=C sort
  )"
  actual_sorted="$(
    printf '%s\n' "${RPM_ACTUAL_REQUIREMENTS[@]}" | LC_ALL=C sort
  )"

  if [ "$actual_sorted" != "$expected_sorted" ]; then
    echo "Expected rpm Requires entries (excluding rpmlib capabilities):" >&2
    printf '  %s\n' "${RPM_EXPECTED_REQUIREMENTS[@]}" >&2
    echo "Actual rpm Requires entries (excluding rpmlib capabilities):" >&2
    printf '  %s\n' "${RPM_ACTUAL_REQUIREMENTS[@]}" >&2
    fail "rpm Requires does not exactly match the required capability set."
  fi
}

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
  # Defined further down, once the container stage is reached; a --validate-only
  # run exits before then.
  if declare -F cleanup_images >/dev/null; then
    cleanup_images
  fi
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
# Staged, not piped: cpio stops reading at the archive trailer, so under `pipefail`
# a still-writing rpm2cpio takes SIGPIPE and fails the gate on a valid RPM.
rpm2cpio "$EXPECTED_RPM" >"$EXTRACT_ROOT/rpm-payload.cpio"
(
  cd "$RPM_EXTRACT"
  cpio --quiet -idmu --no-absolute-filenames <"$EXTRACT_ROOT/rpm-payload.cpio"
)
rm -f "$EXTRACT_ROOT/rpm-payload.cpio"
if ! (
  cd "$APPIMAGE_EXTRACT"
  "$EXPECTED_APPIMAGE" --appimage-extract >appimage-extract.log 2>&1
); then
  cat "$APPIMAGE_EXTRACT/appimage-extract.log" >&2
  fail "AppImage extraction without FUSE failed."
fi

DEB_GLIBC_FLOOR="$(compute_glibc_floor "$DEB_EXTRACT")"
RPM_GLIBC_FLOOR="$(compute_glibc_floor "$RPM_EXTRACT")"
[ "$DEB_GLIBC_FLOOR" = "$RPM_GLIBC_FLOOR" ] \
  || fail "extracted deb GLIBC floor $DEB_GLIBC_FLOOR differs from rpm floor $RPM_GLIBC_FLOOR."
DEB_EXPECTED_DEPENDENCY_GROUPS=(
  "libc6 (>= $DEB_GLIBC_FLOOR)"
  "${DEB_EXPECTED_DEPENDENCY_GROUPS[@]}"
)
RPM_EXPECTED_REQUIREMENTS+=("libc.so.6(GLIBC_$RPM_GLIBC_FLOOR)(64bit)")
echo "==> Extracted payload GLIBC floor: $DEB_GLIBC_FLOOR"
assert_exact_deb_dependencies
assert_rpm_requirements

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
  validate_bundle_identity "$format" "$app_dir/Plugins"
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

# These SONAMEs are intentionally weak: the corresponding integration can be
# absent without preventing startup, and each entry has a working fallback.
declare -A OPTIONAL_ELF_SONAMES=(
  # GLX acceleration is optional because Avalonia can continue with EGL/software.
  ["libGL.so.1"]="optional GLX rendering"
  # The GLX dispatcher is used only by the optional accelerated rendering path.
  ["libGLX.so.0"]="optional GLX vendor dispatch"
  # EGL is the second rendering choice and is not needed by software rendering.
  ["libEGL.so.1"]="optional EGL rendering"
  # Vendor-neutral OpenGL is part of the optional GL/EGL driver stack.
  ["libOpenGL.so.0"]="optional vendor-neutral OpenGL"
  # GLdispatch is driver plumbing for optional GLX/EGL acceleration.
  ["libGLdispatch.so.0"]="optional GLX/EGL vendor dispatch"
  # Xlib/XCB interop is used by optional GLX/EGL integration, not software mode.
  ["libX11-xcb.so.1"]="optional X11 GL interop"
  # PulseAudio is an optional audio backend alongside the hard ALSA/JACK closure.
  ["libpulse.so.0"]="optional PulseAudio backend"
  # The PulseAudio simple API is likewise optional backend support.
  ["libpulse-simple.so.0"]="optional PulseAudio simple API"
  # D-Bus backs optional tray/session desktop integration.
  ["libdbus-1.so.3"]="optional tray and session integration"
  # xkbcommon augments keyboard handling but the X11 path can run without it.
  ["libxkbcommon.so.0"]="optional keyboard mapping"
  # The xkbcommon X11 adapter is optional with the core X11 keyboard path.
  ["libxkbcommon-x11.so.0"]="optional X11 keyboard mapping"
  ["liblttng-ust.so.0"]="optional LTTng CoreCLR trace provider (dlopened only when LTTng tracing is enabled)"
)

soname_is_declared_hard_dependency() {
  local format="$1"
  local soname="$2"
  local deb_prefix group alternative rpm_requirement
  local alternatives

  if [ "$format" = "rpm" ]; then
    case "$soname" in
      libicudata.so.*|libicui18n.so.*|libicuio.so.*|libicutest.so.*|libicutu.so.*|libicuuc.so.*)
        # ICU is intentionally represented by its stable virtual capability.
        array_contains_exact "libicu" "${RPM_EXPECTED_REQUIREMENTS[@]}"
        return
        ;;
    esac

    case "$soname" in
      ld-linux-x86-64.so.2|libanl.so.1|libBrokenLocale.so.1|libdl.so.2|libm.so.6|libpthread.so.0|libresolv.so.2|librt.so.1|libutil.so.1)
        # glibc provides all of these; represented by the libc.so.6 capability.
        array_contains_exact "libc.so.6()(64bit)" "${RPM_EXPECTED_REQUIREMENTS[@]}"
        return
        ;;
    esac

    rpm_requirement="${soname}()(64bit)"
    array_contains_exact "$rpm_requirement" "${RPM_EXPECTED_REQUIREMENTS[@]}"
    return
  fi

  [ "$format" = "deb" ] || return 1

  case "$soname" in
    ld-linux-x86-64.so.2|libanl.so.1|libBrokenLocale.so.1|libc.so.6|libdl.so.2|libm.so.6|libpthread.so.0|libresolv.so.2|librt.so.1|libutil.so.1)
      deb_prefix="libc6"
      ;;
    libgcc_s.so.1)
      deb_prefix="libgcc-s1"
      ;;
    libstdc++.so.6)
      deb_prefix="libstdc++6"
      ;;
    libicudata.so.*|libicui18n.so.*|libicuio.so.*|libicutest.so.*|libicutu.so.*|libicuuc.so.*)
      deb_prefix="libicu"
      ;;
    libfontconfig.so.1|libexpat.so.1|libfreetype.so.6)
      # libfontconfig1/fontconfig guarantees its expat/freetype runtime closure.
      deb_prefix="libfontconfig1"
      ;;
    libX11.so.6|libXau.so.6|libXdmcp.so.6|libxcb.so.1)
      # The X11 provider guarantees its Xau/Xdmcp/XCB runtime closure.
      deb_prefix="libx11-6"
      ;;
    libXcursor.so.1|libXfixes.so.3|libXrender.so.1)
      # Xcursor guarantees its Xfixes/Xrender runtime closure.
      deb_prefix="libxcursor1"
      ;;
    libXext.so.6)
      deb_prefix="libxext6"
      ;;
    libXi.so.6)
      deb_prefix="libxi6"
      ;;
    libXrandr.so.2)
      deb_prefix="libxrandr2"
      ;;
    libICE.so.6)
      deb_prefix="libice6"
      ;;
    libSM.so.6|libuuid.so.1)
      # libSM6/libSM guarantees the libuuid runtime used by session management.
      deb_prefix="libsm6"
      ;;
    libXtst.so.6)
      # SharpHook's libuiohook.so hard-links the Xtst/Xt/Xinerama trio.
      deb_prefix="libxtst6"
      ;;
    libXt.so.6)
      deb_prefix="libxt6"
      ;;
    libXinerama.so.1)
      deb_prefix="libxinerama1"
      ;;
    libssl.so.3|libcrypto.so.3)
      deb_prefix="libssl3"
      ;;
    libz.so.1)
      deb_prefix="zlib1g"
      ;;
    libgomp.so.1)
      deb_prefix="libgomp1"
      ;;
    libgssapi_krb5.so.2)
      deb_prefix="libgssapi-krb5-2"
      ;;
    libasound.so.2)
      deb_prefix="libasound2"
      ;;
    libjack.so.0)
      deb_prefix="libjack"
      ;;
    *) return 1 ;;
  esac

  for group in "${DEB_EXPECTED_DEPENDENCY_GROUPS[@]}"; do
    IFS='|' read -ra alternatives <<<"$group"
    for alternative in "${alternatives[@]}"; do
      alternative="${alternative#"${alternative%%[![:space:]]*}"}"
      alternative="${alternative%"${alternative##*[![:space:]]}"}"
      [[ "$alternative" == "$deb_prefix"* ]] && return 0
    done
  done
  return 1
}

# The single-file CLI's embedded native libraries are invisible to readelf: only
# its apphost DT_NEEDED entries are scanned, and those natives are currently a
# subset of the loose GUI native libraries that are scanned directly.
audit_elf_dependency_closure() {
  local format="$1"
  local app_dir="$2"
  local candidate soname
  local missing=0
  local provided_sonames needed_sonames

  echo "==> Auditing extracted $format ELF dependency closure"
  provided_sonames="$(
    while IFS= read -r -d '' candidate; do
      if readelf -h "$candidate" >/dev/null 2>&1; then
        printf '%s\n' "${candidate##*/}"
        readelf -d "$candidate" 2>/dev/null \
          | sed -nE 's/.*\(SONAME\).*Library soname: \[([^]]+)\].*/\1/p'
      fi
    done < <(find "$app_dir" \( -type f -o -type l \) -print0)
  )"
  needed_sonames="$(
    while IFS= read -r -d '' candidate; do
      if readelf -h "$candidate" >/dev/null 2>&1; then
        readelf -d "$candidate" 2>/dev/null \
          | sed -nE 's/.*\(NEEDED\).*Shared library: \[([^]]+)\].*/\1/p'
      fi
    done < <(find "$app_dir" \( -type f -o -type l \) -print0)
  )"

  while IFS= read -r soname; do
    [ -n "$soname" ] || continue
    if grep -Fxq -- "$soname" <<<"$provided_sonames"; then
      continue
    fi
    if soname_is_declared_hard_dependency "$format" "$soname"; then
      continue
    fi
    if [[ -v "OPTIONAL_ELF_SONAMES[$soname]" ]]; then
      echo "    optional external $soname (${OPTIONAL_ELF_SONAMES[$soname]})"
      continue
    fi

    echo "ERROR: $format payload needs undeclared external SONAME: $soname" >&2
    missing=1
  done < <(printf '%s\n' "$needed_sonames" | LC_ALL=C sort -u)

  [ "$missing" -eq 0 ] \
    || fail "$format payload has external ELF dependencies outside its metadata and optional allowlist."
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
audit_elf_dependency_closure "deb" "$DEB_EXTRACT/opt/typewhisper"

require_executable "$RPM_EXTRACT/usr/bin/typewhisper"
require_executable "$RPM_EXTRACT/usr/bin/typewhisper-cli"
validate_payload \
  "rpm" \
  "$RPM_EXTRACT/opt/typewhisper" \
  "$RPM_EXTRACT/opt/typewhisper/typewhisper" \
  "$RPM_EXTRACT/usr/share/applications/typewhisper.desktop" \
  "$RPM_EXTRACT/usr/share/icons/hicolor/128x128/apps/typewhisper.png"
audit_elf_dependency_closure "rpm" "$RPM_EXTRACT/opt/typewhisper"

echo "==> All host-side metadata and extraction checks passed."
if [ "$MODE" = "--validate-only" ]; then
  echo "==> Container install/execution checks explicitly skipped by --validate-only."
  exit 0
fi

require_command docker
if ! docker info >/dev/null 2>&1; then
  fail "Docker is installed but its daemon is unavailable; container package smoke tests cannot run."
fi

UBUNTU_BASE_IMAGE="ubuntu:24.04"
FEDORA_BASE_IMAGE="fedora:43"
UBUNTU_IMAGE="typewhisper-smoke-ubuntu:$$"
FEDORA_IMAGE="typewhisper-smoke-fedora:$$"
BUILT_IMAGES=()

cleanup_images() {
  local image
  for image in "${BUILT_IMAGES[@]+"${BUILT_IMAGES[@]}"}"; do
    docker image rm --force "$image" >/dev/null 2>&1 || true
  done
}

# Bake the runtime dependencies into one image per distribution up front. The
# resolver-less Ubuntu formats otherwise download the same ~95 MB twice, and
# because that used to happen inside the per-format timeout a slow mirror killed
# the run before a single assertion executed.
build_smoke_image() {
  local distribution="$1"
  local base_image="$2"
  local image="$3"
  local context

  echo "==> Building $distribution smoke image from $base_image"
  context="$(mktemp -d)"
  cp "$SCRIPT_PATH" "$context/smoke-test-linux-packages.sh"
  {
    printf 'FROM %s\n' "$base_image"
    printf 'COPY smoke-test-linux-packages.sh /smoke-test-linux-packages.sh\n'
    printf 'RUN bash /smoke-test-linux-packages.sh --install-runtime %s\n' "$distribution"
  } >"$context/Dockerfile"

  if ! docker build --pull --tag "$image" "$context"; then
    rm -rf "$context"
    fail "failed to build the $distribution smoke image."
  fi
  rm -rf "$context"
  BUILT_IMAGES+=("$image")
}

run_container_smoke() {
  local format="$1"
  local image="$2"
  local package="$3"
  local container_name="typewhisper-package-smoke-${format}-$$"
  local status

  echo "==> Running $format smoke test in image $image"
  # For the prepared images the dependencies are already baked in, so this budget
  # covers only the install/execute assertions. The deb runs on the bare base
  # image and still resolves its own Depends here, which is the point of that
  # format's test.
  set +e
  timeout --signal=INT --kill-after=30s 10m \
    docker run --name "$container_name" --rm \
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

build_smoke_image ubuntu "$UBUNTU_BASE_IMAGE" "$UBUNTU_IMAGE"
build_smoke_image fedora "$FEDORA_BASE_IMAGE" "$FEDORA_IMAGE"

# Keep these sequential so logs identify the failing format and package-manager
# operations never overlap on the runner.
run_container_smoke "tarball" "$UBUNTU_IMAGE" "$EXPECTED_TARBALL"
run_container_smoke "appimage" "$UBUNTU_IMAGE" "$EXPECTED_APPIMAGE"
# The deb runs on the bare base image: the prepared image has the whole runtime
# closure baked in, which would both hide a missing Depends and make the
# assertions that no Recommends-demoted library was pulled in fail outright.
run_container_smoke "deb" "$UBUNTU_BASE_IMAGE" "$EXPECTED_DEB"
run_container_smoke "rpm" "$FEDORA_IMAGE" "$EXPECTED_RPM"

echo "==> All Linux package smoke tests passed."
