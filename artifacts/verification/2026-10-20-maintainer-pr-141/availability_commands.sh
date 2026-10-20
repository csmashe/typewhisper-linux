#!/usr/bin/env bash
set -euo pipefail
cd "$(git rev-parse --show-toplevel)"
evidence="$PWD/artifacts/verification/2026-10-20-maintainer-pr-141"
export DOTNET_ROOT=/tmp/typewhisper-dotnet
export DOTNET="$DOTNET_ROOT/dotnet"
export PATH="$DOTNET_ROOT:/tmp/pr137-tools/xclip/usr/bin:$PATH"
export LD_LIBRARY_PATH="/tmp/pr137-tools/xclip/usr/lib${LD_LIBRARY_PATH:+:$LD_LIBRARY_PATH}"
recipes="$evidence/tools/justfile"
if [[ ! -f "$recipes" ]]; then
    mkdir -p "$evidence/tools"
    tar -xzf "$evidence/verification-tools.tar.gz" -C "$evidence/tools"
fi
just --justfile "$recipes" --working-directory "$PWD" build-release
just --justfile "$recipes" --working-directory "$PWD" test tests/TypeWhisper.Linux.Tests/TypeWhisper.Linux.Tests.csproj
uv run --no-project python -m unittest discover -s "$evidence/tools/tests/verification"
out="$evidence/availability-repeat-$(date +%s)"
uv run --no-project "$evidence/availability_ui.py" --out "$out/ui"
uv run --no-project "$evidence/live_verify.py" --cases spoken-hit --out "$out/audio"
