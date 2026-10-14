#!/usr/bin/env python3
"""End-to-end dictation smoke test against an isolated TypeWhisper instance.

Plays one public clip into a virtual microphone, dictates through the real API path,
optionally routes cleanup to a local fake LLM, and reads the delivered text back from
the clipboard. Writes `live-smoke.json` and `run.json` into the evidence folder.

By default the app uses the user's display, and the user's plain-text clipboard is saved
first and restored afterwards. That mode refuses to start with non-text clipboard content
and stops if the clipboard cannot be read within 5 s. `--desktop hidden` runs the app in a
hidden headless desktop and reads that session's clipboard instead; it has not been proven
live yet. Either way, the app refuses to start dictation while the user's session is
locked, so the script checks the lock first.
"""

from __future__ import annotations

import argparse
import contextlib
import datetime as dt
import json
import os
import statistics
import subprocess
import sys
import time
import urllib.error
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
import hidden_desktop  # noqa: E402
import isolated_app  # noqa: E402

DEFAULT_CORPUS = Path("/tmp/typewhisper-benchmark")
DEFAULT_CLIP = "1462-170145-0012"
TEXT_TYPES = {"UTF8_STRING", "TEXT", "STRING"}


def reference_for(audio: Path) -> str | None:
    for manifest in (audio.parent / "manifest.json",):
        try:
            items = json.loads(manifest.read_text(encoding="utf-8")).get("items", [])
        except (OSError, json.JSONDecodeError, AttributeError):
            continue
        for item in items:
            if item.get("id") == audio.stem:
                return item.get("reference")
    return None


CLIPBOARD_TIMEOUT = 5.0


def parse_locked_hint(output: str) -> bool | None:
    for line in output.splitlines():
        if line.startswith("LockedHint="):
            return line.split("=", 1)[1].strip() == "yes"
    return None


def session_locked() -> bool | None:
    """The app refuses to start dictation while the user's graphical session is locked."""
    try:
        sessions = subprocess.run(["loginctl", "list-sessions", "--no-legend"], capture_output=True, text=True, timeout=5)
        for line in sessions.stdout.splitlines():
            parts = line.split()
            if parts and "seat0" in parts:
                shown = subprocess.run(["loginctl", "show-session", parts[0], "-p", "LockedHint"],
                                       capture_output=True, text=True, timeout=5)
                return parse_locked_hint(shown.stdout)
    except (OSError, subprocess.TimeoutExpired):
        pass
    return None


def paste(env: dict[str, str] | None = None, *args: str) -> subprocess.CompletedProcess:
    """wl-paste with a deadline: on GNOME it needs a focused surface and blocks while the screen is locked."""
    try:
        return subprocess.run(["wl-paste", *args], capture_output=True, timeout=CLIPBOARD_TIMEOUT, env=env)
    except subprocess.TimeoutExpired:
        raise SystemExit("wl-paste did not answer within 5 s. The session cannot read its clipboard, for example "
                         "while the screen is locked. Use the default --desktop hidden.") from None


class HiddenClipboard:
    """The hidden desktop's own clipboard: delivery read-back that never touches the user's."""

    def __init__(self, wayland_display: str):
        self.env = {**os.environ, "WAYLAND_DISPLAY": wayland_display}

    def __enter__(self) -> "HiddenClipboard":
        return self

    def read(self) -> bytes:
        return paste(self.env, "--no-newline").stdout

    def __exit__(self, *exc) -> None:
        pass


class ClipboardGuard:
    """Save the user's plain-text clipboard and put it back after the run (--desktop user)."""

    def __init__(self):
        self.saved: bytes | None = None
        self.had_content = False

    def __enter__(self) -> "ClipboardGuard":
        types = paste(None, "--list-types")
        if types.returncode == 0 and types.stdout.strip():
            offered = types.stdout.decode(errors="replace").split()
            if not all(t.startswith("text/plain") or t in TEXT_TYPES for t in offered):
                raise SystemExit(f"The clipboard holds non-text data ({', '.join(offered)}). Copy plain text or clear it, then rerun.")
            self.saved = paste(None, "--no-newline").stdout
            self.had_content = True
        return self

    def read(self) -> bytes:
        return paste(None, "--no-newline").stdout

    def __exit__(self, *exc) -> None:
        # wl-copy keeps a child serving the selection. Detach it from our pipes so a
        # caller reading our output does not wait for the user's next copy.
        detached = {"stdout": subprocess.DEVNULL, "stderr": subprocess.DEVNULL, "start_new_session": True}
        if self.had_content and self.saved is not None:
            subprocess.run(["wl-copy", "--type", "text/plain;charset=utf-8"], input=self.saved, check=True, **detached)
        else:
            subprocess.run(["wl-copy", "--clear"], check=False, **detached)


TIMING_FIELDS = ("transcription_milliseconds", "queue_wait_milliseconds", "delivery_milliseconds",
                 "stop_to_delivery_milliseconds")


def session_checks(record: dict, live_cleanup: bool) -> dict[str, bool]:
    result = record.get("result") or {}
    performance = result.get("performance") or {}
    checks = {
        "text_ready": result.get("state") == "ready" and bool(result.get("text")),
        "delivered_to_clipboard": performance.get("delivery_outcome") == "CopiedToClipboard",
        "clipboard_read_back_matches": bool(record.get("clipboard_matches")),
        "llm_route_correct": not record.get("llm_rejected_paths"),
    }
    if live_cleanup:
        checks["live_cleanup_ran_before_stop"] = record.get("llm_requests_before_stop", 0) > 0
    return checks


def summarize_timings(records: list[dict]) -> dict[str, dict[str, float]]:
    """Median and worst value per stage across sessions, including each processing step."""
    samples: dict[str, list[float]] = {}
    for record in records:
        performance = (record.get("result") or {}).get("performance") or {}
        for field in TIMING_FIELDS:
            if isinstance(performance.get(field), (int, float)):
                samples.setdefault(field, []).append(float(performance[field]))
        for step, value in (performance.get("processing_milliseconds") or {}).items():
            samples.setdefault(f"step.{step}", []).append(float(value))
    return {name: {"median": round(statistics.median(values), 1), "max": round(max(values), 1), "n": len(values)}
            for name, values in samples.items()}


def dictate_once(app: "isolated_app.IsolatedApp", llm, clipboard: "ClipboardGuard", audio: Path, tail: float) -> dict:
    llm_before = len(llm.requests) if llm else 0
    started_at = time.time()
    started = app.api("/v1/dictation/start", {})
    time.sleep(0.3)
    sources = app.capture_sources()
    if not sources or any(source != app.mic.source for source in sources):
        raise AssertionError(f"app is not recording only from the fixture: {sources}")
    app.mic.play(audio)
    time.sleep(tail)
    requests_before_stop = (len(llm.requests) - llm_before) if llm else 0
    stop_at = time.time()
    stopped = app.api("/v1/dictation/stop", {})
    path = f"/v1/dictation/transcription?sessionId={started['session_id']}"
    result = app.api(path)
    deadline = time.monotonic() + 15
    while result.get("performance") is None and time.monotonic() < deadline:
        time.sleep(0.25)
        result = app.api(path)
    delivered = clipboard.read().decode(errors="replace")
    return {"started": started, "stopped": stopped, "result": result, "start_at": started_at, "stop_at": stop_at,
            "capture_sources": sources, "clipboard_text": delivered,
            "clipboard_matches": bool(delivered.strip()) and delivered.strip() == str(result.get("text") or "").strip(),
            "llm_requests_before_stop": requests_before_stop,
            "llm_requests_total": (len(llm.requests) - llm_before) if llm else 0,
            "llm_rejected_paths": list(llm.rejected_paths) if llm else []}


def run(args: argparse.Namespace) -> int:
    evidence = args.out.resolve()
    evidence.mkdir(parents=True, exist_ok=True)
    audio = args.audio.resolve()
    if not audio.is_file():
        raise SystemExit(f"{audio} is missing. Prepare the public corpus first (just prepare-corpus).")
    if session_locked():
        raise SystemExit("The desktop session is locked, and TypeWhisper refuses to start dictation while locked. "
                         "Unlock the session and rerun. UI checks (just ui-check, just ui-live) work while locked.")

    settings: dict[str, object] = {
        "selectedModelId": f"plugin:{args.plugin}:{args.model}",
        "liveTranscriptionEnabled": True,
        "liveCleanupEnabled": args.llm == "fake" and args.live_cleanup,
        "cleanupLevel": 2 if args.llm == "fake" else 0,
        "pluginEnabledState": {"com.typewhisper.filler-words": False},
    }
    plugin_settings: dict[str, dict[str, object]] = {}
    llm = isolated_app.FakeLlm(delay_seconds=args.llm_delay) if args.llm == "fake" else None
    evidence_record: dict[str, object] = {"audio": str(audio), "reference": reference_for(audio)}

    with contextlib.ExitStack() as stack:
        # The hidden desktop gives the app its own display and clipboard: delivery read-back works
        # while the user's screen is locked and never writes the user's clipboard.
        if args.desktop == "hidden":
            desktop = stack.enter_context(hidden_desktop.HiddenDesktop(evidence / "desktop"))
            display_env = desktop.app_env()
            clipboard = stack.enter_context(HiddenClipboard(display_env["WAYLAND_DISPLAY"]))
            display = "none"
        else:
            display_env, display = {}, "inherit"
            clipboard = stack.enter_context(ClipboardGuard())
        evidence_record["desktop"] = args.desktop
        if llm is not None:
            stack.enter_context(llm)
            app_settings, plugin = llm.plugin_overrides()
            enabled = {**settings["pluginEnabledState"], **app_settings.pop("pluginEnabledState")}
            settings.update(app_settings, pluginEnabledState=enabled)
            plugin_settings["com.typewhisper.openai-compatible"] = plugin
        with isolated_app.IsolatedApp(evidence, configuration=args.configuration, settings=settings,
                                      plugin_data=[args.plugin], plugin_settings=plugin_settings,
                                      virtual_mic=True, display=display, extra_env=display_env) as app:
            isolated_app.write_json(evidence / "app-status.json", app.status)
            evidence_record["api_token_rotated_by_app"] = app.token_rotated_by_app
            evidence_record["model_load"] = app.load_model(args.engine, args.model)
            evidence_record["llm"] = (f"fake endpoint, {args.llm_delay * 1000:.0f} ms delay, not a real model"
                                      if llm else "none")
            sessions = []
            for _ in range(args.repeat):
                sessions.append(dictate_once(app, llm, clipboard, audio, args.tail_seconds))
                time.sleep(0.5)
            evidence_record["sessions"] = sessions
            status = app.api("/v1/status")
            acceleration = status.get("acceleration") or {}

    isolated_app.write_json(evidence / "live-smoke.json", evidence_record)
    isolated_app.write_json(evidence / "run.json", isolated_app.run_manifest({
        "kind": "live-smoke", "configuration": args.configuration, "engine": args.engine, "model": args.model,
        "backend": acceleration.get("active_backend"), "llm": evidence_record["llm"],
        "live_cleanup": settings["liveCleanupEnabled"], "audio": audio.name, "sessions": args.repeat,
    }))

    live_cleanup = llm is not None and bool(settings["liveCleanupEnabled"])
    per_session = [session_checks(record, live_cleanup) for record in evidence_record["sessions"]]
    checks = {name: all(check[name] for check in per_session) for name in per_session[0]}
    timings = summarize_timings(evidence_record["sessions"])
    isolated_app.write_json(evidence / "timings.json", timings)
    last = evidence_record["sessions"][-1]["result"]
    summary = {"checks": checks, "sessions": args.repeat, "text": last.get("text"), "reference": evidence_record["reference"],
               "timings_ms": timings, "evidence": str(evidence)}
    print(json.dumps(summary, indent=2))
    return 0 if all(checks.values()) else 1


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("--out", type=Path, default=Path(f"artifacts/verification/{dt.date.today()}-live-smoke"))
    parser.add_argument("--audio", type=Path, default=DEFAULT_CORPUS / f"{DEFAULT_CLIP}.wav")
    parser.add_argument("--configuration", default="Debug", choices=("Debug", "Release"))
    parser.add_argument("--plugin", default="com.typewhisper.sherpa-onnx")
    parser.add_argument("--engine", default="sherpa-onnx")
    parser.add_argument("--model", default="parakeet-tdt-0.6b")
    parser.add_argument("--llm", default="fake", choices=("fake", "none"))
    parser.add_argument("--desktop", default="user", choices=("user", "hidden"),
                        help="user: the user's display and clipboard, saved and restored (default, proven); "
                             "hidden: the app draws and delivers into a hidden headless desktop, never touching "
                             "the user's clipboard (not yet proven live)")
    parser.add_argument("--llm-delay", type=float, default=0.25)
    parser.add_argument("--no-live-cleanup", dest="live_cleanup", action="store_false")
    parser.add_argument("--repeat", type=int, default=1, help="dictations in one warm instance; timings report median and max")
    parser.add_argument("--tail-seconds", type=float, default=4.0,
                        help="silence recorded after the clip ends; live cleanup waits 2 s after the last transcript "
                             "update (LiveCleanupSession debounce), so shorter tails skip the draft request")
    args = parser.parse_args()
    try:
        return run(args)
    except Exception as error:
        import traceback

        failure = {"error": f"{type(error).__name__}: {error}", "traceback": traceback.format_exc()}
        if isinstance(error, urllib.error.HTTPError):
            failure["http_body"] = error.read().decode(errors="replace")
        isolated_app.write_json(args.out.resolve() / "failure.json", failure)
        print(failure["traceback"], file=sys.stderr)
        return 1


if __name__ == "__main__":
    sys.exit(main())
