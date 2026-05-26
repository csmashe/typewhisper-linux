#!/usr/bin/env bash
# Deploy the Linux-capable plugins into the TypeWhisper.Linux build output's
# Plugins/ subdirectory. Run after building TypeWhisper.Linux so the bundle
# ships alongside the app; BundledPluginDeployer will auto-install them on
# first run into $XDG_DATA_HOME/TypeWhisper/Plugins/.
#
# Usage:
#   scripts/deploy-linux-plugins.sh [Release|Debug] [version]
#
# The optional version arg must match whatever version was passed to the host
# publish (-p:Version=...). It propagates to PluginSDK and every plugin so the
# host's loaded PluginSDK.dll and the plugins' AssemblyRef to PluginSDK agree
# on AssemblyVersion. Mismatch → plugins fail to type-load at runtime because
# PluginAssemblyLoadContext redirects PluginSDK references to the host's copy
# and the version doesn't satisfy the bind.
#
# Environment:
#   TYPEWHISPER_PLUGIN_PUBLISH_JOBS=<n>  Max concurrent plugin publishes.
#                                       Defaults to 4.
#
# Idempotent — safe to re-run.

set -euo pipefail

CONFIG="${1:-Release}"
VERSION="${2:-}"

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

# Plugin ID (manifest id) → plugin project name
declare -A PLUGINS=(
  ["com.typewhisper.sherpa-onnx"]="TypeWhisper.Plugin.SherpaOnnx"
  ["com.typewhisper.whisper-cpp"]="TypeWhisper.Plugin.WhisperCpp"
  ["com.typewhisper.file-memory"]="TypeWhisper.Plugin.FileMemory"
  ["com.typewhisper.openai"]="TypeWhisper.Plugin.OpenAi"
  ["com.typewhisper.openrouter"]="TypeWhisper.Plugin.OpenRouter"
  ["com.typewhisper.gemini"]="TypeWhisper.Plugin.Gemini"
  ["com.typewhisper.cerebras"]="TypeWhisper.Plugin.Cerebras"
  ["com.typewhisper.claude"]="TypeWhisper.Plugin.Claude"
  ["com.typewhisper.cohere"]="TypeWhisper.Plugin.Cohere"
  ["com.typewhisper.fireworks"]="TypeWhisper.Plugin.Fireworks"
  ["com.typewhisper.groq"]="TypeWhisper.Plugin.Groq"
  ["com.typewhisper.xai"]="TypeWhisper.Plugin.Xai"
  ["com.typewhisper.supertonic-tts"]="TypeWhisper.Plugin.SupertonicTts"
  ["com.typewhisper.assemblyai"]="TypeWhisper.Plugin.AssemblyAi"
  ["com.typewhisper.deepgram"]="TypeWhisper.Plugin.Deepgram"
  ["com.typewhisper.smallest-ai"]="TypeWhisper.Plugin.SmallestAi"
  ["com.typewhisper.elevenlabs"]="TypeWhisper.Plugin.ElevenLabs"
  ["com.typewhisper.cloudflare-asr"]="TypeWhisper.Plugin.CloudflareAsr"
  ["com.typewhisper.gladia"]="TypeWhisper.Plugin.Gladia"
  ["com.typewhisper.speechmatics"]="TypeWhisper.Plugin.Speechmatics"
  ["com.typewhisper.soniox"]="TypeWhisper.Plugin.Soniox"
  ["com.typewhisper.google-cloud-stt"]="TypeWhisper.Plugin.GoogleCloudStt"
  ["com.typewhisper.voxtral"]="TypeWhisper.Plugin.Voxtral"
  ["com.typewhisper.qwen3-stt"]="TypeWhisper.Plugin.Qwen3Stt"
  ["com.typewhisper.obsidian"]="TypeWhisper.Plugin.Obsidian"
  ["com.typewhisper.linear"]="TypeWhisper.Plugin.Linear"
  ["com.typewhisper.openai-compatible"]="TypeWhisper.Plugin.OpenAiCompatible"
  ["com.typewhisper.gemma-local"]="TypeWhisper.Plugin.GemmaLocal"
  ["com.typewhisper.openai-vector-memory"]="TypeWhisper.Plugin.OpenAiVectorMemory"
  ["com.typewhisper.script"]="TypeWhisper.Plugin.Script"
  ["com.typewhisper.webhook"]="TypeWhisper.Plugin.Webhook"
)

# Clean any plugins lingering from previous builds. The script's PLUGINS array
# is the authoritative manifest; anything else in $OUT is a stale artifact from
# an earlier script revision (or a hand-deployed plugin) that would otherwise
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
for id in "${!PLUGINS[@]}"; do
  project="${PLUGINS[$id]}"
  dotnet restore "$ROOT/plugins/$project/$project.csproj" \
    -r "$RID" "${VERSION_ARG[@]}" --nologo -v quiet > /dev/null
done

publish_plugin() {
  local id="$1"
  project="${PLUGINS[$id]}"
  proj_dir="$ROOT/plugins/$project"
  pub_dir="$proj_dir/bin/$CONFIG/net10.0/$RID/publish"
  dest="$OUT/$id"
  log_file="$TMP_DIR/${id//\//_}.log"

  {
    echo "==> $id ($project)"
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

for id in "${!PLUGINS[@]}"; do
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

for id in "${!PLUGINS[@]}"; do
  sed -n '1,200p' "$TMP_DIR/${id//\//_}.log"
done

echo ""
echo "Done. Bundled plugins are in: $OUT"
echo "On first run, BundledPluginDeployer will copy them into ~/.local/share/TypeWhisper/Plugins/"
