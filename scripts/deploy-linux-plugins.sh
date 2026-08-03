#!/usr/bin/env bash
# Deploy the Linux-capable plugins into the TypeWhisper.Linux build output's
# Plugins/ subdirectory. Run after building TypeWhisper.Linux so the bundle
# ships alongside the app; BundledPluginDeployer will auto-install them on
# first run into $XDG_DATA_HOME/TypeWhisper/Plugins/.
#
# Usage:
#   scripts/deploy-linux-plugins.sh [Release|Debug] [version]
#   scripts/deploy-linux-plugins.sh --validate-only
#
# The optional version arg must match whatever version was passed to the host
# publish (-p:Version=...). It propagates to PluginSDK and every plugin so the
# host's loaded PluginSDK.dll and the plugins' AssemblyRef to PluginSDK agree
# on AssemblyVersion. Mismatch → plugins fail to type-load at runtime because
# PluginAssemblyLoadContext redirects PluginSDK references to the host's copy
# and the version doesn't satisfy the bind.
#
# Requires PowerShell 7 (pwsh): plugins/catalog.json is the authoritative plugin
# list and scripts/plugin-catalog.ps1 is what validates it and turns it into this
# script's deploy map. TypeWhisper.Linux runs this from an AfterTargets="Build"
# target, so `dotnet build` needs pwsh too; build with
# -p:DeployBundledLinuxPlugins=false to skip plugin bundling instead.
#
# Environment:
#   TYPEWHISPER_PLUGIN_PUBLISH_JOBS=<n>  Max concurrent plugin publishes.
#                                       Defaults to 4.
#
# Idempotent — safe to re-run.

set -euo pipefail

VALIDATE_ONLY=false
if [ "${1:-}" = "--validate-only" ]; then
  if [ "$#" -ne 1 ]; then
    echo "ERROR: --validate-only does not accept other arguments." >&2
    exit 2
  fi
  VALIDATE_ONLY=true
  CONFIG="Release"
  VERSION=""
else
  if [ "$#" -gt 2 ]; then
    echo "ERROR: expected [Release|Debug] [version] or --validate-only." >&2
    exit 2
  fi
  CONFIG="${1:-Release}"
  VERSION="${2:-}"
fi

# Reject anything that isn't a real msbuild Configuration before letting it
# feed into OUT — the script does rm -rf "$OUT" and we don't want "../.."
# slipping through that.
if [ "$CONFIG" != "Release" ] && [ "$CONFIG" != "Debug" ]; then
  echo "ERROR: CONFIG must be 'Release' or 'Debug' (got '$CONFIG')." >&2
  exit 2
fi

RID="linux-x64"
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
OUT="$ROOT/src/TypeWhisper.Linux/bin/$CONFIG/net10.0/Plugins"
JOBS="${TYPEWHISPER_PLUGIN_PUBLISH_JOBS:-4}"
TMP_DIR="$(mktemp -d)"

# Only pass -p:Version when explicitly given. Empty string would override the
# Directory.Build.props default with "" and produce a build error.
VERSION_ARG=()
if [ -n "$VERSION" ]; then
  VERSION_ARG=(-p:Version="$VERSION")
fi

cleanup() {
  rm -rf "$TMP_DIR"
}

trap cleanup EXIT

# Generate the Linux deployment view from the canonical catalog. Write it to a
# real file before sourcing so a pwsh/validation failure propagates reliably;
# process-substitution exit codes are otherwise easy for bash to miss.
if ! command -v pwsh > /dev/null 2>&1; then
  echo "ERROR: pwsh is required to validate and read plugins/catalog.json." >&2
  echo "       Install PowerShell 7, or build without bundled plugins:" >&2
  echo "       dotnet build -p:DeployBundledLinuxPlugins=false" >&2
  exit 1
fi

CATALOG_MAP="$TMP_DIR/plugin-deploy-map.sh"
if ! pwsh -NoProfile -File "$ROOT/scripts/plugin-catalog.ps1" \
  -View DeployMap -Platform linux -Rid "$RID" > "$CATALOG_MAP"; then
  echo "ERROR: plugin catalog validation or deploy-map generation failed." >&2
  exit 1
fi
# shellcheck source=/dev/null
source "$CATALOG_MAP"

if [ "${#PLUGIN_IDS[@]}" -eq 0 ]; then
  echo "ERROR: plugin catalog generated an empty Linux deploy map." >&2
  exit 1
fi

if [ "$VALIDATE_ONLY" = true ]; then
  echo "Validated ${#PLUGIN_IDS[@]} Linux plugin catalog entries for $RID."
  exit 0
fi

# Clean any plugins lingering from previous builds. The catalog-generated map
# is authoritative; anything else in $OUT is a stale artifact from an earlier
# catalog revision (or a hand-deployed plugin) that would otherwise
# get bundled into the package with whatever PluginSDK version it was last
# built against — usually mismatched with the host and a hard load failure.
rm -rf "$OUT"
mkdir -p "$OUT"

# Build the shared PluginSDK once up front so the parallel plugin publishes
# below can skip rebuilding it (-p:BuildProjectReferences=false). Concurrent
# builds otherwise race and corrupt PluginSDK's obj/ intermediates. The RID is
# deliberately omitted: it does not propagate to this project reference, so the
# plugin publishes resolve the non-RID ref assembly under obj/$CONFIG/net10.0/.
dotnet build "$ROOT/src/TypeWhisper.PluginSDK/TypeWhisper.PluginSDK.csproj" \
  -c "$CONFIG" -f net10.0 "${VERSION_ARG[@]}" --nologo -v quiet > /dev/null

# Sequentially restore each plugin up front. -p:BuildProjectReferences=false
# stops MSBuild from re-building PluginSDK during the parallel publish below,
# but it does NOT stop NuGet from walking project references during restore.
# Without this pre-restore, parallel plugin publishes race to rewrite
# src/TypeWhisper.PluginSDK/obj/project.assets.json. Combined with --no-restore
# in publish_plugin below, restore happens exactly once per project and the
# parallel phase is restore-free.
for id in "${PLUGIN_IDS[@]}"; do
  project_path="${PLUGINS[$id]}"
  dotnet restore "$ROOT/$project_path" \
    -r "$RID" "${VERSION_ARG[@]}" --nologo -v quiet > /dev/null
done

publish_plugin() {
  local id="$1"
  local project_path="${PLUGINS[$id]}"
  local project="${project_path##*/}"
  project="${project%.csproj}"
  local proj_dir="$ROOT/${project_path%/*}"
  local pub_dir="$proj_dir/bin/$CONFIG/net10.0/$RID/publish"
  local dest="$OUT/$id"
  local log_file="$TMP_DIR/${id//\//_}.log"

  {
    echo "==> $id ($project)"
    # Wipe the per-plugin publish output before each run so a removed source
    # file (e.g. a deleted reference, renamed embedded resource) doesn't
    # survive in the publish dir and get copied into the final bundle.
    # dotnet publish copies new outputs over the old but does not prune.
    rm -rf "$pub_dir"
    dotnet publish "$proj_dir/$project.csproj" -c "$CONFIG" -f net10.0 -r "$RID" \
      --self-contained false -p:BuildProjectReferences=false --no-restore \
      "${VERSION_ARG[@]}" --nologo -v quiet

    rm -rf "$dest"
    mkdir -p "$dest"

    # Copy everything except the host-provided PluginSDK (would shadow host
    # types) and .pdb symbols.
    for item in "$pub_dir"/*; do
      name=$(basename "$item")
      case "$name" in
        TypeWhisper.PluginSDK.dll|TypeWhisper.PluginSDK.pdb) continue ;;
        *.pdb) continue ;;
      esac
      cp -r "$item" "$dest/"
    done

    echo "    -> $dest"
  } >"$log_file" 2>&1
}

throttle_jobs() {
  while [ "$(jobs -pr | wc -l)" -ge "$JOBS" ]; do
    sleep 0.1
  done
}

declare -a PIDS=()
declare -a IDS=()

for id in "${PLUGIN_IDS[@]}"; do
  throttle_jobs
  publish_plugin "$id" &
  PIDS+=("$!")
  IDS+=("$id")
done

status=0
for i in "${!PIDS[@]}"; do
  pid="${PIDS[$i]}"
  id="${IDS[$i]}"
  if ! wait "$pid"; then
    status=1
    echo "ERROR: publish failed for $id" >&2
    sed -n '1,200p' "$TMP_DIR/${id//\//_}.log" >&2 || true
  fi
done

if [ "$status" -ne 0 ]; then
  exit "$status"
fi

for id in "${PLUGIN_IDS[@]}"; do
  sed -n '1,200p' "$TMP_DIR/${id//\//_}.log"
done

echo ""
echo "Done. Bundled plugins are in: $OUT"
echo "On first run, BundledPluginDeployer will copy them into ~/.local/share/TypeWhisper/Plugins/"
