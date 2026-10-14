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

test-all: test-core test-plugins test-linux test-cli test-integration

# Full Linux CI build and test set, including restore for a fresh checkout.
ci: restore build-release test-all

doctor:
    uv run --no-project scripts/verification/doctor.py

prepare-corpus:
    uv run --no-project scripts/verification/prepare_librispeech.py

# Start the app API, select and load the requested model/backend in Settings,
# then run `just benchmark`. Results contain transcript text and stay local.
benchmark engine="sherpa-onnx" model="parakeet-tdt-0.6b-v3" backend="webgpu" language="en" cli="src/TypeWhisper.Cli/bin/Debug/net10.0/typewhisper-cli":
    mkdir -p "artifacts/verification/$(date +%F)-{{backend}}"
    uv run --no-project scripts/verification/benchmark_transcription.py --manifest benchmarks/data/librispeech-50.jsonl --engine "{{engine}}" --model "{{model}}" --expect-backend "{{backend}}" --language "{{language}}" --cli "{{cli}}" --output "artifacts/verification/$(date +%F)-{{backend}}/benchmark.json"
