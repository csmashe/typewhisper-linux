#!/usr/bin/env bash
# Build all Linux distribution packages from a fresh publish of TypeWhisper.Linux.
# Used by .github/workflows/release-linux.yml and package-dry-run-linux.yml so
# both flows produce bit-identical artifacts. Also runnable locally.
#
# Usage:
#   scripts/build-linux-packages.sh <version> [output-dir]
#
# Produces in <output-dir> (default: dist/):
#   typewhisper-linux-x64-<version>.tar.gz
#   TypeWhisper-<version>-x86_64.AppImage
#   typewhisper_<version>_amd64.deb
#   typewhisper-<version>-1.x86_64.rpm
#
# Tooling required: dotnet, ffmpeg-free tools not required, plus:
#   tar gzip                 (tar.gz)
#   sha256sum sort readelf   (plugin identity + dependency floors — hard required, checked up front)
#   wget, appstreamcli       (AppImage helpers — only wget is hard required)
#   dpkg-deb                 (.deb)
#   rpmbuild                 (.rpm)
# Missing tools: the format is skipped with a warning, not a hard fail.
#
# Idempotent: each format builds into its own staging dir under a temp root.

set -euo pipefail

VERSION="${1:-}"
OUTPUT_DIR="${2:-dist}"

if [ -z "$VERSION" ]; then
  echo "Usage: $0 <version> [output-dir]" >&2
  exit 2
fi

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
CONFIG="Release"
RID="linux-x64"
APP_ID="typewhisper"
APP_NAME="TypeWhisper"
PROJECT="$ROOT/src/TypeWhisper.Linux/TypeWhisper.Linux.csproj"
PUBLISH_DIR="$ROOT/src/TypeWhisper.Linux/bin/$CONFIG/net10.0/$RID/publish"
CLI_PROJECT="$ROOT/src/TypeWhisper.Cli/TypeWhisper.Cli.csproj"
CLI_PUBLISH_DIR="$ROOT/src/TypeWhisper.Cli/bin/$CONFIG/net10.0/$RID/publish"
ICON_SRC="$ROOT/src/TypeWhisper.Linux/Resources/typewhisper-128.png"
BUNDLE_IDENTITY_FILE_NAME=".typewhisper-bundle-identity.sha256"

# Every format carries the bundled plugin identity, so unlike per-format tooling
# these are checked before the long publish rather than skipped with a warning.
for required in sha256sum sort readelf; do
  command -v "$required" >/dev/null 2>&1 \
    || { echo "ERROR: $required is required for plugin identity and dependency floors" >&2; exit 1; }
done

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
  # and silently under-floor the packages. grep exit 1 stays tolerated — it
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

# The GLIBCXX floor is deliberately not encoded: symbol-to-package-version mapping is distro-specific, and staged-verify/smoke containers run at-or-above the build host.

mkdir -p "$OUTPUT_DIR"
OUTPUT_DIR="$(cd "$OUTPUT_DIR" && pwd)"

STAGE_ROOT="$(mktemp -d)"
trap 'rm -rf "$STAGE_ROOT"' EXIT

echo "==> Publishing TypeWhisper.Linux ($CONFIG, $RID, version $VERSION)"
dotnet publish "$PROJECT" \
  -c "$CONFIG" \
  -r "$RID" \
  --self-contained true \
  -p:Version="$VERSION" \
  -p:PublishSingleFile=false \
  -p:PublishReadyToRun=true \
  -p:DeployBundledLinuxPlugins=false \
  -p:DebugType=None \
  -p:DebugSymbols=false \
  --nologo

echo "==> Publishing TypeWhisper.Cli ($CONFIG, $RID, version $VERSION)"
dotnet publish "$CLI_PROJECT" \
  -c "$CONFIG" \
  -r "$RID" \
  --self-contained true \
  -p:Version="$VERSION" \
  -p:PublishSingleFile=true \
  -p:IncludeNativeLibrariesForSelfExtract=true \
  -p:DebugType=None \
  -p:DebugSymbols=false \
  --nologo

rm -rf "$PUBLISH_DIR/Cli"
mkdir -p "$PUBLISH_DIR/Cli"
cp "$CLI_PUBLISH_DIR/typewhisper-cli" "$PUBLISH_DIR/Cli/typewhisper-cli"
chmod 0755 "$PUBLISH_DIR/Cli/typewhisper-cli"

echo "==> Bundling Linux plugins"
# Pass VERSION so PluginSDK and plugins build with the same AssemblyVersion as
# the host. Otherwise plugins reference PluginSDK at the Directory.Build.props
# default and the host loads it at our $VERSION; AssemblyLoadContext can't
# satisfy the version-bound AssemblyRef and every plugin fails to type-load.
bash "$ROOT/scripts/deploy-linux-plugins.sh" "$CONFIG" "$VERSION"

# Copy bundled plugins into publish output (mirrors what install-linux-app.sh does).
if [ -d "$ROOT/src/TypeWhisper.Linux/bin/$CONFIG/net10.0/Plugins" ]; then
  rm -rf "$PUBLISH_DIR/Plugins"
  cp -R "$ROOT/src/TypeWhisper.Linux/bin/$CONFIG/net10.0/Plugins" "$PUBLISH_DIR/Plugins"
fi

if [ ! -x "$PUBLISH_DIR/typewhisper" ]; then
  chmod +x "$PUBLISH_DIR/typewhisper"
fi

# Whisper.net (and a couple of other plugin deps) bundle native libs for every
# RID they support. For a linux-x64 build, the linux-arm/linux-arm64/win/macos/osx
# directories are dead weight (~50 MB per package format) and they also break
# rpmbuild's auto-strip phase, which can't read non-host ELF formats.
# Note: whisper.cpp names its Apple runtime dirs "macos-*", while SkiaSharp and the
# .NET runtime pack use "osx-*" — prune both families.
echo "==> Pruning non-linux-x64 native runtimes"
find "$PUBLISH_DIR" -type d \
  \( -name "linux-arm" -o -name "linux-arm64" -o -name "linux-musl-*" \
     -o -name "linux-x86" -o -name "linux-loongarch64" -o -name "linux-riscv64" \
     -o -name "win-*" -o -name "osx-*" -o -name "macos-*" \
     -o -name "browser-*" -o -name "android-*" \
  \) -prune -exec rm -rf {} +

# Some ONNX Runtime consumers (sherpa-onnx, supertonic-tts) also drop the
# Windows-native onnxruntime DLLs flat into the output root and per-plugin dirs,
# not under a runtimes/win-* directory the prune above would catch. They are PE
# binaries with no use on Linux — the managed Microsoft.ML.OnnxRuntime.dll loads
# libonnxruntime.so instead — so strip them too. Match case-insensitively so a
# differently-cased native drop (e.g. OnnxRuntime.dll) is pruned too; the glob is
# anchored at the filename start, so it still never matches the managed wrapper,
# which keeps its Microsoft. prefix.
find "$PUBLISH_DIR" -type f -iname "onnxruntime*.dll" -delete

# This publish-time marker attests to the final, post-prune Plugins payload. The
# runtime trusts it as the source identity, so keep path ordering deterministic
# and exclude only the marker itself from the content-derived fingerprint.
BUNDLE_IDENTITY_PATH="$PUBLISH_DIR/Plugins/$BUNDLE_IDENTITY_FILE_NAME"
rm -f "$BUNDLE_IDENTITY_PATH"
compute_bundle_identity "$PUBLISH_DIR/Plugins" >"$BUNDLE_IDENTITY_PATH"
# The deployer must read this as any user; don't inherit a restrictive umask.
chmod 0644 "$BUNDLE_IDENTITY_PATH"

# ---------- tar.gz (the JustWorks fallback) ----------
echo "==> Building tar.gz"
TARBALL_NAME="typewhisper-linux-x64-${VERSION}"
TARBALL_STAGE="$STAGE_ROOT/$TARBALL_NAME"
mkdir -p "$TARBALL_STAGE"
cp -R "$PUBLISH_DIR/." "$TARBALL_STAGE/"
cp "$ICON_SRC" "$TARBALL_STAGE/typewhisper.png"

cat > "$TARBALL_STAGE/typewhisper.desktop" <<EOF
[Desktop Entry]
Type=Application
Version=1.0
Name=$APP_NAME
GenericName=Voice-to-text dictation
Comment=Speech-to-text dictation for Linux desktop
Exec=typewhisper
Icon=typewhisper
Terminal=false
Categories=Utility;Accessibility;AudioVideo;
StartupNotify=true
StartupWMClass=typewhisper
EOF

mkdir -p "$TARBALL_STAGE/lib"
cp "$ROOT/scripts/tarball-install.sh" "$TARBALL_STAGE/install.sh"
cp "$ROOT/scripts/lib/managed-artifacts.sh" "$TARBALL_STAGE/lib/managed-artifacts.sh"
chmod +x "$TARBALL_STAGE/install.sh"

tar -czf "$OUTPUT_DIR/${TARBALL_NAME}.tar.gz" -C "$STAGE_ROOT" "$TARBALL_NAME"
echo "    -> $OUTPUT_DIR/${TARBALL_NAME}.tar.gz"

# ---------- AppImage ----------
# Need both a downloader (wget) and a checksum verifier (sha256sum) — skipping
# the SHA verification on a download-from-the-internet step is not acceptable
# even on stripped environments.
if command -v wget >/dev/null 2>&1 && command -v sha256sum >/dev/null 2>&1; then
  echo "==> Building AppImage"
  APPDIR="$STAGE_ROOT/TypeWhisper.AppDir"
  mkdir -p "$APPDIR/usr/bin" "$APPDIR/usr/share/applications" "$APPDIR/usr/share/icons/hicolor/128x128/apps"

  cp -R "$PUBLISH_DIR/." "$APPDIR/usr/bin/"
  chmod +x "$APPDIR/usr/bin/typewhisper"
  cp "$ICON_SRC" "$APPDIR/usr/share/icons/hicolor/128x128/apps/typewhisper.png"
  cp "$ICON_SRC" "$APPDIR/typewhisper.png"

  cat > "$APPDIR/typewhisper.desktop" <<EOF
[Desktop Entry]
Type=Application
Version=1.0
Name=$APP_NAME
GenericName=Voice-to-text dictation
Comment=Speech-to-text dictation for Linux desktop
Exec=typewhisper
Icon=typewhisper
Terminal=false
Categories=Utility;Accessibility;AudioVideo;
StartupNotify=true
StartupWMClass=typewhisper
EOF
  cp "$APPDIR/typewhisper.desktop" "$APPDIR/usr/share/applications/typewhisper.desktop"

  cat > "$APPDIR/AppRun" <<'EOF'
#!/usr/bin/env bash
HERE="$(cd "$(dirname "$(readlink -f "$0")")" && pwd)"
export LD_LIBRARY_PATH="$HERE/usr/bin:${LD_LIBRARY_PATH:-}"
exec "$HERE/usr/bin/typewhisper" "$@"
EOF
  chmod +x "$APPDIR/AppRun"

  # Pin to a tagged release and verify SHA256 before executing — the previous
  # "continuous" URL was a moving target, which is a supply-chain risk for
  # release artifacts built from it. SHA matches the asset digest exposed by
  # GitHub's release API for AppImage/appimagetool 1.9.1.
  APPIMAGETOOL_VERSION="1.9.1"
  APPIMAGETOOL_SHA256="ed4ce84f0d9caff66f50bcca6ff6f35aae54ce8135408b3fa33abfc3cb384eb0"
  APPIMAGETOOL_URL="https://github.com/AppImage/appimagetool/releases/download/${APPIMAGETOOL_VERSION}/appimagetool-x86_64.AppImage"
  APPIMAGETOOL="$STAGE_ROOT/appimagetool"
  wget -qO "$APPIMAGETOOL" "$APPIMAGETOOL_URL"
  echo "${APPIMAGETOOL_SHA256}  ${APPIMAGETOOL}" | sha256sum --check --status \
    || { echo "ERROR: appimagetool checksum mismatch — aborting AppImage build" >&2; exit 1; }
  chmod +x "$APPIMAGETOOL"

  APPIMAGE_OUT="$OUTPUT_DIR/${APP_NAME}-${VERSION}-x86_64.AppImage"
  ARCH=x86_64 "$APPIMAGETOOL" --appimage-extract-and-run "$APPDIR" "$APPIMAGE_OUT"
  echo "    -> $APPIMAGE_OUT"
else
  echo "WARN: wget or sha256sum not available; skipping AppImage" >&2
fi

# ---------- .deb ----------
DEB_GLIBC_FLOOR=""
if command -v dpkg-deb >/dev/null 2>&1; then
  echo "==> Building .deb"
  DEB_STAGE="$STAGE_ROOT/deb"
  mkdir -p \
    "$DEB_STAGE/DEBIAN" \
    "$DEB_STAGE/opt/typewhisper" \
    "$DEB_STAGE/usr/bin" \
    "$DEB_STAGE/usr/share/applications" \
    "$DEB_STAGE/usr/share/icons/hicolor/128x128/apps"

  cp -R "$PUBLISH_DIR/." "$DEB_STAGE/opt/typewhisper/"
  chmod +x "$DEB_STAGE/opt/typewhisper/typewhisper"
  cp "$ICON_SRC" "$DEB_STAGE/usr/share/icons/hicolor/128x128/apps/typewhisper.png"

  cat > "$DEB_STAGE/usr/bin/typewhisper" <<'EOF'
#!/usr/bin/env bash
exec /opt/typewhisper/typewhisper "$@"
EOF
  chmod 0755 "$DEB_STAGE/usr/bin/typewhisper"

  cat > "$DEB_STAGE/usr/bin/typewhisper-cli" <<'EOF'
#!/usr/bin/env bash
exec /opt/typewhisper/Cli/typewhisper-cli "$@"
EOF
  chmod 0755 "$DEB_STAGE/usr/bin/typewhisper-cli"

  cat > "$DEB_STAGE/usr/share/applications/typewhisper.desktop" <<EOF
[Desktop Entry]
Type=Application
Version=1.0
Name=$APP_NAME
GenericName=Voice-to-text dictation
Comment=Speech-to-text dictation for Linux desktop
Exec=typewhisper
Icon=typewhisper
Terminal=false
Categories=Utility;Accessibility;AudioVideo;
StartupNotify=true
StartupWMClass=typewhisper
EOF

  # Strip a leading 'v' if a caller passed a tag; Debian package versions don't take one.
  DEB_VERSION="${VERSION#v}"
  INSTALLED_SIZE="$(du -sk "$DEB_STAGE/opt/typewhisper" | cut -f1)"
  DEB_GLIBC_FLOOR="$(compute_glibc_floor "$DEB_STAGE")"

  # Keep this list literal: dpkg-shlibdeps cannot see the ICU, OpenSSL, and
  # GSSAPI libraries that self-contained .NET resolves with dlopen at runtime.
  cat > "$DEB_STAGE/DEBIAN/control" <<EOF
Package: typewhisper
Version: $DEB_VERSION
Section: utils
Priority: optional
Architecture: amd64
Maintainer: Excel on the Web <noreply@excelontheweb.com>
Installed-Size: $INSTALLED_SIZE
Depends: libc6 (>= $DEB_GLIBC_FLOOR), libgcc-s1, libstdc++6, libicu78 | libicu76 | libicu74 | libicu72 | libicu70, libfontconfig1, libx11-6, libxcursor1, libxext6, libxi6, libxrandr2, libice6, libsm6, libxtst6, libxt6t64 | libxt6, libxinerama1, libssl3t64 | libssl3, zlib1g, libgomp1, libgssapi-krb5-2, ca-certificates, tzdata, libasound2t64 | libasound2, libjack-jackd2-0 | libjack0 | pipewire-jack
Recommends: libgl1, libegl1, libpulse0, pulseaudio-utils, playerctl, xdotool, libdbus-1-3, dbus-x11, libglib2.0-bin, libxkbcommon0, libxkbcommon-x11-0
Description: Speech-to-text dictation for Linux desktop
 TypeWhisper provides global dictation, file transcription, recorder,
 dictionary/snippets, and pluggable speech and LLM providers for Linux.
EOF

  cat > "$DEB_STAGE/DEBIAN/postinst" <<'EOF'
#!/bin/sh
set -e
if command -v update-desktop-database >/dev/null 2>&1; then
  update-desktop-database /usr/share/applications >/dev/null 2>&1 || true
fi
if command -v gtk-update-icon-cache >/dev/null 2>&1; then
  gtk-update-icon-cache /usr/share/icons/hicolor >/dev/null 2>&1 || true
fi
EOF
  chmod 0755 "$DEB_STAGE/DEBIAN/postinst"

  DEB_OUT="$OUTPUT_DIR/typewhisper_${DEB_VERSION}_amd64.deb"
  dpkg-deb --root-owner-group --build "$DEB_STAGE" "$DEB_OUT" >/dev/null
  echo "    -> $DEB_OUT"
else
  echo "WARN: dpkg-deb not available; skipping .deb" >&2
fi

# ---------- .rpm ----------
if command -v rpmbuild >/dev/null 2>&1; then
  echo "==> Building .rpm"
  RPM_VERSION="${VERSION#v}"
  # rpm version cannot contain '-'; replace with '~' for prerelease segments.
  RPM_VERSION_CLEAN="${RPM_VERSION//-/\~}"

  RPM_TOP="$STAGE_ROOT/rpmbuild"
  mkdir -p "$RPM_TOP"/{BUILD,RPMS,SOURCES,SPECS,SRPMS,BUILDROOT}

  RPM_SRC="$RPM_TOP/SOURCES/typewhisper-$RPM_VERSION_CLEAN"
  mkdir -p "$RPM_SRC/opt/typewhisper" "$RPM_SRC/usr/bin" "$RPM_SRC/usr/share/applications" "$RPM_SRC/usr/share/icons/hicolor/128x128/apps"
  cp -R "$PUBLISH_DIR/." "$RPM_SRC/opt/typewhisper/"
  chmod +x "$RPM_SRC/opt/typewhisper/typewhisper"
  cp "$ICON_SRC" "$RPM_SRC/usr/share/icons/hicolor/128x128/apps/typewhisper.png"

  cat > "$RPM_SRC/usr/bin/typewhisper" <<'EOF'
#!/usr/bin/env bash
exec /opt/typewhisper/typewhisper "$@"
EOF
  chmod 0755 "$RPM_SRC/usr/bin/typewhisper"

  cat > "$RPM_SRC/usr/bin/typewhisper-cli" <<'EOF'
#!/usr/bin/env bash
exec /opt/typewhisper/Cli/typewhisper-cli "$@"
EOF
  chmod 0755 "$RPM_SRC/usr/bin/typewhisper-cli"

  cat > "$RPM_SRC/usr/share/applications/typewhisper.desktop" <<EOF
[Desktop Entry]
Type=Application
Version=1.0
Name=$APP_NAME
GenericName=Voice-to-text dictation
Comment=Speech-to-text dictation for Linux desktop
Exec=typewhisper
Icon=typewhisper
Terminal=false
Categories=Utility;Accessibility;AudioVideo;
StartupNotify=true
StartupWMClass=typewhisper
EOF

  RPM_GLIBC_FLOOR="$(compute_glibc_floor "$RPM_SRC")"
  if [ -n "$DEB_GLIBC_FLOOR" ] && [ "$RPM_GLIBC_FLOOR" != "$DEB_GLIBC_FLOOR" ]; then
    echo "ERROR: staged deb GLIBC floor $DEB_GLIBC_FLOOR differs from rpm floor $RPM_GLIBC_FLOOR" >&2
    exit 1
  fi

  tar -czf "$RPM_TOP/SOURCES/typewhisper-$RPM_VERSION_CLEAN.tar.gz" -C "$RPM_TOP/SOURCES" "typewhisper-$RPM_VERSION_CLEAN"

  cat > "$RPM_TOP/SPECS/typewhisper.spec" <<EOF
# Self-contained .NET app + native plugin libs: skip rpm's auto debuginfo
# extraction and binary stripping. .NET single-file/self-contained payloads
# and bundled .so files aren't candidates for distro-style debug splitting.
%global debug_package %{nil}
%global __strip /bin/true
%global __os_install_post %{nil}

Name:           typewhisper
Version:        $RPM_VERSION_CLEAN
Release:        1%{?dist}
Summary:        Speech-to-text dictation for Linux desktop
License:        GPL-3.0-or-later
URL:            https://github.com/csmashe/typewhisper-linux
Source0:        %{name}-%{version}.tar.gz
BuildArch:      x86_64
AutoReqProv:    no
# Keep this list literal: AutoReq cannot see the ICU, OpenSSL, and GSSAPI
# libraries that self-contained .NET resolves with dlopen at runtime. SONAME
# capabilities also let alternative providers satisfy the native dependencies.
# /bin/sh is added automatically as the %post interpreter requirement and is
# asserted in RPM_EXPECTED_REQUIREMENTS; declaring it here would create a
# second requirement tuple with different flags and break set-equality.
Requires:       libc.so.6()(64bit)
Requires:       libc.so.6(GLIBC_$RPM_GLIBC_FLOOR)(64bit)
Requires:       libgcc_s.so.1()(64bit)
Requires:       libstdc++.so.6()(64bit)
Requires:       libicu
Requires:       libfontconfig.so.1()(64bit)
Requires:       libX11.so.6()(64bit)
Requires:       libXcursor.so.1()(64bit)
Requires:       libXext.so.6()(64bit)
Requires:       libXi.so.6()(64bit)
Requires:       libXrandr.so.2()(64bit)
Requires:       libICE.so.6()(64bit)
Requires:       libSM.so.6()(64bit)
Requires:       libXtst.so.6()(64bit)
Requires:       libXt.so.6()(64bit)
Requires:       libXinerama.so.1()(64bit)
Requires:       libssl.so.3()(64bit)
Requires:       libcrypto.so.3()(64bit)
Requires:       libz.so.1()(64bit)
Requires:       libgomp.so.1()(64bit)
Requires:       libgssapi_krb5.so.2()(64bit)
Requires:       ca-certificates
Requires:       tzdata
Requires:       libjack.so.0()(64bit)
Requires:       libasound.so.2()(64bit)
Recommends:     libglvnd-glx, libglvnd-egl, pulseaudio-libs, pulseaudio-utils, playerctl, xdotool, dbus-libs, dbus-daemon, glib2, libxkbcommon, libxkbcommon-x11

%description
TypeWhisper provides global dictation, file transcription, recorder,
dictionary/snippets, and pluggable speech and LLM providers for Linux.

%prep
%setup -q

%install
mkdir -p %{buildroot}
cp -a opt %{buildroot}/
cp -a usr %{buildroot}/

%files
/opt/typewhisper
/usr/bin/typewhisper
/usr/bin/typewhisper-cli
/usr/share/applications/typewhisper.desktop
/usr/share/icons/hicolor/128x128/apps/typewhisper.png

%post
if command -v update-desktop-database >/dev/null 2>&1; then
  update-desktop-database /usr/share/applications >/dev/null 2>&1 || true
fi
if command -v gtk-update-icon-cache >/dev/null 2>&1; then
  gtk-update-icon-cache /usr/share/icons/hicolor >/dev/null 2>&1 || true
fi
EOF

  rpmbuild --define "_topdir $RPM_TOP" -bb "$RPM_TOP/SPECS/typewhisper.spec" >/dev/null
  RPM_BUILT="$(find "$RPM_TOP/RPMS" -name "typewhisper-*.rpm" -type f | head -1)"
  RPM_OUT="$OUTPUT_DIR/typewhisper-${RPM_VERSION_CLEAN}-1.x86_64.rpm"
  cp "$RPM_BUILT" "$RPM_OUT"
  echo "    -> $RPM_OUT"
else
  echo "WARN: rpmbuild not available; skipping .rpm" >&2
fi

echo ""
echo "All packages in: $OUTPUT_DIR"
ls -lh "$OUTPUT_DIR"
