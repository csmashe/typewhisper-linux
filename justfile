set shell := ["bash", "-eu", "-o", "pipefail", "-c"]

# Respect an explicit SDK path, then use the normal PATH. The /tmp fallback is
# for the repository's current local SDK install.
dotnet := env_var_or_default("DOTNET", `if command -v dotnet >/dev/null 2>&1; then command -v dotnet; elif [ -x /tmp/typewhisper-dotnet/dotnet ]; then printf '%s' /tmp/typewhisper-dotnet/dotnet; else printf '%s' dotnet; fi`)
test_results := "artifacts/test-results"

default:
    @just --list

# Fast local Debug build. Bundle available plugins beside the app binary.
build:
    DOTNET="{{dotnet}}" "{{dotnet}}" build TypeWhisper.slnx -c Debug
    "{{dotnet}}" build src/TypeWhisper.Cli/TypeWhisper.Cli.csproj -c Debug --no-restore

restore:
    "{{dotnet}}" restore TypeWhisper.slnx
    "{{dotnet}}" restore src/TypeWhisper.Cli/TypeWhisper.Cli.csproj

build-release:
    DOTNET="{{dotnet}}" "{{dotnet}}" build TypeWhisper.slnx -c Release --no-restore
    "{{dotnet}}" build src/TypeWhisper.Cli/TypeWhisper.Cli.csproj -c Release --no-restore

# Rebuild and restart the local Debug app with the bundled plugin set.
restart: build
    DOTNET="{{dotnet}}" bash scripts/restart-local-app.sh

# Run one named CI test project, for example: just test tests/TypeWhisper.Core.Tests/TypeWhisper.Core.Tests.csproj
test project:
    mkdir -p "{{test_results}}"
    "{{dotnet}}" test "{{project}}" -c Release --no-build --logger "trx" --results-directory "{{test_results}}" --blame-hang --blame-hang-timeout 5m --blame-hang-dump-type mini

test-core:
    just test tests/TypeWhisper.Core.Tests/TypeWhisper.Core.Tests.csproj

test-plugins:
    just test tests/TypeWhisper.PluginSystem.Tests/TypeWhisper.PluginSystem.Tests.csproj

test-linux:
    just test tests/TypeWhisper.Linux.Tests/TypeWhisper.Linux.Tests.csproj

test-cli:
    just test tests/TypeWhisper.Cli.Tests/TypeWhisper.Cli.Tests.csproj

test-integration:
    mkdir -p "{{test_results}}"
    "{{dotnet}}" test tests/TypeWhisper.Integration.Tests/TypeWhisper.Integration.Tests.csproj -c Release --no-build --logger "trx;LogFileName=TypeWhisper.Integration.Tests.trx" --results-directory "{{test_results}}" --blame-hang --blame-hang-timeout 2m --blame-hang-dump-type mini

# Rendered UI checks; PNGs and findings go to artifacts/ui-snapshots/latest.
test-ui:
    just ui-check artifacts/ui-snapshots/latest

test-all: test-core test-plugins test-linux test-cli test-integration test-ui

# Full Linux CI build and test set, including restore for a fresh checkout.
ci: restore build-release test-all

doctor:
    uv run --no-project scripts/verification/doctor.py

# End-to-end dictation in an isolated instance: public clip into a virtual mic,
# fake cleanup LLM, clipboard read-back. Leaves the running app untouched.
# Build Release first. Arguments are positional: just live-smoke <out> "--llm none".
live-smoke out=("artifacts/verification/" + `date +%F` + "-live-smoke") args="":
    uv run --no-project scripts/verification/live_smoke.py --configuration Release --out "{{out}}" {{args}}

# Render every Settings section headlessly per UI language and window size. Writes PNGs
# and findings JSON to out; fails on clipped text, raw localization keys, or unnamed controls.
ui-check out=("artifacts/verification/" + `date +%F` + "-ui"):
    mkdir -p "{{out}}" "{{test_results}}"
    TW_UI_SNAPSHOT_DIR="{{out}}" "{{dotnet}}" test tests/TypeWhisper.UiTests/TypeWhisper.UiTests.csproj -c Release --no-build --logger "trx;LogFileName=TypeWhisper.UiTests.trx" --results-directory "{{test_results}}" --blame-hang --blame-hang-timeout 5m --blame-hang-dump-type mini

# Open the isolated app in a hidden headless mutter session (private D-Bus and AT-SPI),
# click Open Wizard through the accessibility tree, and save screenshots of both states.
ui-live out=("artifacts/verification/" + `date +%F` + "-hidden-desktop"):
    uv run --no-project scripts/verification/hidden_desktop.py --out "{{out}}"

# Warm end-to-end dictation timings: n dictations in one isolated instance, median and max
# per stage (transcription, each processing step, queue, delivery). Fake LLM, public clip.
perf n="5" out=("artifacts/verification/" + `date +%F` + "-perf"):
    uv run --no-project scripts/verification/live_smoke.py --configuration Release --repeat {{n}} --out "{{out}}"

# Every verification layer that needs no credentials, against the current Release build.
verify: eval-tests
    just doctor || true
    just live-smoke
    just ui-check
    just ui-live

# Time the first live-preview text shown in the overlay while a public clip plays into the
# virtual mic, inside the hidden desktop. Delivery goes to the hidden session's clipboard.
ui-streaming out=("artifacts/verification/" + `date +%F` + "-streaming") audio="/tmp/typewhisper-benchmark/1462-170145-0012.wav":
    uv run --no-project scripts/verification/hidden_desktop.py --mode streaming --out "{{out}}" --audio "{{audio}}"

# Record a main dictation hotkey through Settings > Shortcuts in the hidden desktop, using
# mutter RemoteDesktop input (never the real keyboard), and check the saved setting.
ui-shortcuts out=("artifacts/verification/" + `date +%F` + "-shortcuts") shortcut="Ctrl+Shift+F8":
    uv run --no-project scripts/verification/hidden_desktop.py --mode shortcuts --out "{{out}}" --shortcut "{{shortcut}}"

# Capture eval results through /v1/text/process on an isolated Release instance.
# Uses a local fake LLM: proves the capture path and rule steps, not cleanup quality.
eval-capture out args="":
    uv run --no-project scripts/verification/capture_text_evals.py --out "{{out}}" {{args}}

# Local quality-eval tool checks. No model calls.
eval-tests:
    uv run --no-project python -m unittest discover -s tests/verification

# Create a pending capture template; existing outputs are preserved.
eval-template out:
    uv run --no-project python scripts/verification/dictation_evals.py template --out "{{out}}"

eval-review results out:
    uv run --no-project python scripts/verification/dictation_evals.py review --results "{{results}}" --out "{{out}}"

eval-score review human out:
    uv run --no-project python scripts/verification/dictation_evals.py score --review "{{review}}" --human "{{human}}" --out "{{out}}"

# Explicit opt-in: send completed cases to the selected judge endpoint.
eval-judge review endpoint model out:
    uv run --no-project python scripts/verification/dictation_evals.py judge --review "{{review}}" --endpoint "{{endpoint}}" --model "{{model}}" --out "{{out}}"

prepare-corpus:
    uv run --no-project scripts/verification/prepare_librispeech.py

# Start the app API, select and load the requested model/backend in Settings,
# then run `just benchmark`. Results contain transcript text and stay local.
benchmark engine="sherpa-onnx" model="parakeet-tdt-0.6b" backend="webgpu" language="en" cli="src/TypeWhisper.Cli/bin/Debug/net10.0/typewhisper-cli":
    mkdir -p "artifacts/verification/$(date +%F)-{{backend}}"
    uv run --no-project scripts/verification/benchmark_transcription.py --manifest benchmarks/data/librispeech-50.jsonl --engine "{{engine}}" --model "{{model}}" --expect-backend "{{backend}}" --language "{{language}}" --cli "{{cli}}" --output "artifacts/verification/$(date +%F)-{{backend}}/benchmark.json"
