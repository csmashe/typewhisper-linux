#!/usr/bin/env bash
set -euo pipefail
cd "$(git rev-parse --show-toplevel)"
evidence="$PWD/artifacts/verification/2026-10-20-maintainer-pr-141"
export DOTNET_ROOT=/tmp/typewhisper-dotnet
export PATH="$DOTNET_ROOT:/tmp/pr137-tools/xclip/usr/bin:$PATH"
export LD_LIBRARY_PATH="/tmp/pr137-tools/xclip/usr/lib${LD_LIBRARY_PATH:+:$LD_LIBRARY_PATH}"

# These helpers are exactly from 4f3aa26dc1d0a1c955f7347b9d5dd97c00f7e5ec.
# They remain under evidence, outside the production tree.
mkdir -p "$evidence/tools"
tar -xzf "$evidence/verification-tools.tar.gz" -C "$evidence/tools"
recipes="$evidence/tools/justfile"
just --justfile "$recipes" --working-directory "$PWD" restore
just --justfile "$recipes" --working-directory "$PWD" build-release
for project in Core PluginSystem Linux Cli Integration; do
    just --justfile "$recipes" --working-directory "$PWD" test "tests/TypeWhisper.$project.Tests/TypeWhisper.$project.Tests.csproj"
done
uv run --no-project python -m unittest discover -s "$evidence/tools/tests/verification"
dotnet build "$evidence/observer" -c Release

# Requires mutter, its headless dependencies, ffmpeg, flite, PulseAudio tools,
# system Python GI/AT-SPI bindings, and xclip. No external LLM credentials.
# The saved public source clip is LibriSpeech test-clean 1462-170145-0012.
# If absent, the original corpus helper can fetch it from OpenSLR resources/12.
if [[ ! -f /tmp/typewhisper-benchmark/1462-170145-0012.wav ]]; then
    uv run --no-project "$evidence/tools/scripts/verification/prepare_librispeech.py"
fi
uv run --no-project "$evidence/live_verify.py" --out "$evidence/repeat-$(date +%s)"
uv run --no-project "$evidence/ui_controls.py" --out "$evidence/repeat-ui-$(date +%s)"
