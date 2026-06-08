#!/usr/bin/env bash
# Shared helpers for the TypeWhisper setup-test snapshot/reset tooling.
# Sourced by snapshot.sh and reset.sh — not meant to be run directly.

# ---- locations -------------------------------------------------------------

XDG_DATA_HOME_RESOLVED="${XDG_DATA_HOME:-$HOME/.local/share}"
XDG_CONFIG_HOME_RESOLVED="${XDG_CONFIG_HOME:-$HOME/.config}"
XDG_CACHE_HOME_RESOLVED="${XDG_CACHE_HOME:-$HOME/.cache}"

BASELINE_DIR="$XDG_CACHE_HOME_RESOLVED/typewhisper-setup-test"
BASELINE_ENV="$BASELINE_DIR/baseline.env"
BASELINE_PKGS_ABSENT="$BASELINE_DIR/packages-absent.txt"

# Everything TypeWhisper writes that we may need to undo.
APP_DATA_DIR="$XDG_DATA_HOME_RESOLVED/TypeWhisper"
APP_MODELS_DIR="$APP_DATA_DIR/Models"
CLI_LAUNCHER="$HOME/.local/bin/typewhisper"
AUTOSTART_FILE="$XDG_CONFIG_HOME_RESOLVED/autostart/typewhisper.desktop"
ENVIRONMENTD_FILE="$XDG_CONFIG_HOME_RESOLVED/environment.d/typewhisper-accessibility.conf"
YDOTOOL_UNIT="$XDG_CONFIG_HOME_RESOLVED/systemd/user/ydotoold.service"
YDOTOOL_UDEV_RULE="/etc/udev/rules.d/60-ydotool.rules"
KGLOBALACCEL_DIR="$XDG_DATA_HOME_RESOLVED/kglobalaccel"

# Files TypeWhisper stamps with this marker on the first line; only files that
# contain it are ever removed by reset (so we never delete a user's own file
# that happens to live at the same path).
MARKER="Installed by TypeWhisper"

# Packages the setup can install. wl-clipboard / xclip / ffmpeg are commonly
# pre-installed, which is exactly why reset is baseline-guarded.
TARGET_PACKAGES=(ydotool wl-clipboard xclip xdotool wtype ffmpeg)

# Window Calls GNOME extension — detected via its D-Bus object, removed only if
# it was absent at baseline.
WINDOW_CALLS_DBUS_PATH="/org/gnome/Shell/Extensions/Windows"

# ---- pretty output ---------------------------------------------------------

c_reset=$'\e[0m'; c_bold=$'\e[1m'; c_red=$'\e[31m'; c_grn=$'\e[32m'; c_ylw=$'\e[33m'; c_blu=$'\e[34m'
info()  { printf '%s•%s %s\n' "$c_blu" "$c_reset" "$*"; }
ok()    { printf '%s✓%s %s\n' "$c_grn" "$c_reset" "$*"; }
warn()  { printf '%s!%s %s\n' "$c_ylw" "$c_reset" "$*"; }
err()   { printf '%s✗%s %s\n' "$c_red" "$c_reset" "$*" >&2; }
head1() { printf '\n%s%s%s\n' "$c_bold" "$*" "$c_reset"; }

# ---- package manager -------------------------------------------------------

# Echo one of: dnf | apt | pacman | zypper | unknown
detect_pkg_mgr() {
    local id="" like=""
    if [[ -r /etc/os-release ]]; then
        id=$(. /etc/os-release 2>/dev/null && echo "${ID:-}")
        like=$(. /etc/os-release 2>/dev/null && echo "${ID_LIKE:-}")
    fi
    case "$id $like" in
        *fedora*|*rhel*|*centos*|*rocky*|*almalinux*) command -v dnf >/dev/null && { echo dnf; return; } ;;
        *debian*|*ubuntu*|*mint*)                     command -v apt-get >/dev/null && { echo apt; return; } ;;
        *arch*|*manjaro*)                             command -v pacman >/dev/null && { echo pacman; return; } ;;
        *suse*|*opensuse*)                            command -v zypper >/dev/null && { echo zypper; return; } ;;
    esac
    # Fall back to whatever is on PATH.
    command -v dnf    >/dev/null && { echo dnf;    return; }
    command -v apt-get>/dev/null && { echo apt;    return; }
    command -v pacman >/dev/null && { echo pacman; return; }
    command -v zypper >/dev/null && { echo zypper; return; }
    echo unknown
}

# pkg_installed <mgr> <pkg>  -> exit 0 if installed
pkg_installed() {
    local mgr="$1" pkg="$2"
    case "$mgr" in
        dnf|zypper) rpm -q --quiet "$pkg" ;;
        apt)        dpkg-query -W -f='${Status}' "$pkg" 2>/dev/null | grep -q "install ok installed" ;;
        pacman)     pacman -Qq "$pkg" >/dev/null 2>&1 ;;
        *)          return 1 ;;
    esac
}

# Echo the pkexec remove command prefix for the manager (no packages appended).
pkg_remove_cmd_prefix() {
    case "$1" in
        dnf)    echo "pkexec dnf remove -y" ;;
        apt)    echo "pkexec apt-get remove -y" ;;
        pacman) echo "pkexec pacman -Rns --noconfirm" ;;
        zypper) echo "pkexec zypper --non-interactive remove" ;;
        *)      echo "" ;;
    esac
}

# ---- detectors used by both scripts ---------------------------------------

window_calls_present() {
    command -v gdbus >/dev/null 2>&1 || return 1
    # `introspect` succeeds on ANY path under org.gnome.Shell (false positive),
    # so actually CALL the List method — it errors when absent. Check BOTH the
    # original "Window Calls" and the "Window Calls Extended" fork endpoints.
    gdbus call --session --dest org.gnome.Shell \
        --object-path /org/gnome/Shell/Extensions/Windows \
        --method org.gnome.Shell.Extensions.Windows.List >/dev/null 2>&1 && return 0
    gdbus call --session --dest org.gnome.Shell \
        --object-path /org/gnome/Shell/Extensions/WindowsExt \
        --method org.gnome.Shell.Extensions.WindowsExt.List >/dev/null 2>&1
}
