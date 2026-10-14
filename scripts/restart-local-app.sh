#!/usr/bin/env bash
# Restart the local Debug app after its build has completed.
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
DOTNET="${DOTNET:-dotnet}"
APP_DLL="$ROOT/src/TypeWhisper.Linux/bin/Debug/net10.0/typewhisper.dll"

if [ ! -f "$APP_DLL" ]; then
  echo "ERROR: Debug app assembly is missing: $APP_DLL" >&2
  echo "       Run 'just build' first." >&2
  exit 1
fi

mapfile -t APP_PIDS < <(
  {
    pgrep -x typewhisper || true
    pgrep -f '(^|/)typewhisper\.dll([[:space:]]|$)' || true
  } | sort -u
)

for pid in "${APP_PIDS[@]}"; do
  kill -TERM "$pid"
done

for pid in "${APP_PIDS[@]}"; do
  for _ in {1..150}; do
    if ! kill -0 "$pid" 2>/dev/null; then
      break
    fi
    sleep 0.1
  done

  if kill -0 "$pid" 2>/dev/null; then
    echo "ERROR: TypeWhisper process $pid did not exit after SIGTERM; not starting another instance." >&2
    exit 1
  fi
done

STATE_HOME="${XDG_STATE_HOME:-${HOME:?}/.local/state}"
LOG_DIR="$STATE_HOME/typewhisper"
LOG_FILE="$LOG_DIR/local-dev.log"
mkdir -p "$LOG_DIR"

setsid "$DOTNET" "$APP_DLL" >"$LOG_FILE" 2>&1 </dev/null &
APP_PID=$!

ready=false
for _ in {1..100}; do
  if ! kill -0 "$APP_PID" 2>/dev/null; then
    echo "ERROR: TypeWhisper exited during startup. Recent log output:" >&2
    tail -n 80 "$LOG_FILE" >&2 || true
    exit 1
  fi

  if "$DOTNET" "$APP_DLL" status >/dev/null 2>&1; then
    ready=true
    break
  fi
  sleep 0.2
done

if [ "$ready" != true ]; then
  echo "ERROR: TypeWhisper did not become ready within 20 seconds. Recent log output:" >&2
  tail -n 80 "$LOG_FILE" >&2 || true
  exit 1
fi

echo "Started local Debug TypeWhisper (PID $APP_PID)."
echo "Log: $LOG_FILE"
