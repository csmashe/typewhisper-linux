#!/usr/bin/env bash
# Deploy the Linux-capable plugins into the TypeWhisper.Linux build output's
# Plugins/ subdirectory. Run after building TypeWhisper.Linux so the bundle
# ships alongside the app; BundledPluginDeployer will auto-install them on
# first run into $XDG_DATA_HOME/TypeWhisper/Plugins/.
#
# Usage:
#   scripts/deploy-linux-plugins.sh [Release|Debug]
#
# Idempotent — safe to re-run.

set -euo pipefail

CONFIG="${1:-Release}"
RID="linux-x64"
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
OUT="$ROOT/src/TypeWhisper.Linux/bin/$CONFIG/net10.0/Plugins"

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
  ["com.typewhisper.assemblyai"]="TypeWhisper.Plugin.AssemblyAi"
  ["com.typewhisper.deepgram"]="TypeWhisper.Plugin.Deepgram"
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
)

mkdir -p "$OUT"

for id in "${!PLUGINS[@]}"; do
  project="${PLUGINS[$id]}"
  proj_dir="$ROOT/plugins/$project"
  pub_dir="$proj_dir/bin/$CONFIG/net10.0/$RID/publish"
  dest="$OUT/$id"

  echo "==> $id ($project)"
  dotnet publish "$proj_dir/$project.csproj" -c "$CONFIG" -f net10.0 -r "$RID" --self-contained false --nologo -v quiet > /dev/null

  rm -rf "$dest"
  mkdir -p "$dest"

  # Copy everything except the host-provided PluginSDK (would shadow host types)
  # and .pdb symbols.
  for item in "$pub_dir"/*; do
    name=$(basename "$item")
    case "$name" in
      TypeWhisper.PluginSDK.dll|TypeWhisper.PluginSDK.pdb) continue ;;
      *.pdb) continue ;;
    esac
    cp -r "$item" "$dest/"
  done

  echo "    -> $dest"
done

echo ""
echo "Done. Bundled plugins are in: $OUT"
echo "On first run, BundledPluginDeployer will copy them into ~/.local/share/TypeWhisper/Plugins/"
