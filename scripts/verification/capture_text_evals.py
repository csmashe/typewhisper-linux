#!/usr/bin/env python3
"""Capture dictation eval results through `/v1/text/process` on an isolated instance.

Each case's `input` runs through the app's final dictation pipeline: profile matching,
spoken formatting, cleanup, snippets, dictionary, and profile formatting. The output
rows use the `just eval-template` format, so `just eval-review` reads them directly.

Cleanup goes to a local fake LLM that echoes its input. That run proves the capture path
and rule-based steps, not cleanup quality. A real-provider run needs that provider's
credentials in the isolated instance, which this script does not copy.
"""

from __future__ import annotations

import argparse
import datetime as dt
import json
import sys
import time
import urllib.error
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
import isolated_app  # noqa: E402

ROOT = Path(__file__).resolve().parents[2]
CLEANUP_LEVELS = {"none": 0, "light": 1, "medium": 2, "high": 3}
LLM_LEVELS = {"Medium", "High"}
CANARY = "um this is a readiness check"


def wait_for_cleanup_provider(app: "isolated_app.IsolatedApp", timeout: float = 30.0) -> float:
    """Plugins register LLM providers after the API is up. Wait until cleanup reaches the LLM."""
    started = time.monotonic()
    while True:
        response = app.api("/v1/text/process", {"text": CANARY})
        if response.get("cleanup_level") not in LLM_LEVELS or any(
                call.get("stage") == "Cleanup" and call.get("succeeded", True) for call in response.get("llm_calls") or []):
            return time.monotonic() - started
        if time.monotonic() - started > timeout:
            raise TimeoutError(f"cleanup provider never answered within {timeout:.0f}s; see the instance app.log")
        time.sleep(0.5)


def read_jsonl(path: Path) -> list[dict]:
    return [json.loads(line) for line in path.read_text(encoding="utf-8").splitlines() if line.strip()]


def result_row(case: dict, response: dict | None, error: str | None, latency_ms: float | None, llm: str) -> dict:
    calls = (response or {}).get("llm_calls") or []
    # `succeeded` is true only for a nonempty completed response; an attempt alone is not cleanup.
    cleanup_call = next((call for call in calls if call.get("stage") == "Cleanup" and call.get("succeeded", True)), None)
    if error is None and (response or {}).get("cleanup_level") in LLM_LEVELS and cleanup_call is None:
        # LlmCleanupService returns Light-cleaned text when the provider fails or is missing.
        error = f"{response['cleanup_level']} cleanup fell back to Light: no cleanup LLM call was made"
    profile = case.get("profile", "plain")
    return {
        "id": case["id"],
        "status": "error" if error else "completed",
        "raw_text": case["input"],
        "text": None if error else response["text"],
        "error": error,
        "metadata": {
            "transcription_model": None,
            "backend": None,
            "cleanup_provider": cleanup_call["provider"] if cleanup_call else None,
            "cleanup_model": cleanup_call["model"] if cleanup_call else None,
            "cleanup_level": (response or {}).get("cleanup_level"),
            "prompt_version": None,
            "profile": profile,
            # Case profiles and dictionaries describe intended conditions; this run does not create them.
            "profile_applied": profile == "plain",
            "dictionary_version": None,
            "audio_case": None,
            "latency_ms": latency_ms,
            "capture_route": "/v1/text/process",
            "llm": llm,
            "steps": (response or {}).get("steps"),
        },
    }


def run(args: argparse.Namespace) -> int:
    out = args.out.resolve()
    if out.exists():
        raise SystemExit(f"{out} exists; choose a new path to preserve captured outputs")
    cases = read_jsonl(args.cases)
    workdir = out.parent / (out.stem + "-instance")
    llm_label = f"fake echo endpoint, {args.llm_delay * 1000:.0f} ms delay, not a real model"
    rows = []
    with isolated_app.FakeLlm(delay_seconds=args.llm_delay) as llm:
        app_settings, plugin = llm.plugin_overrides()
        settings = {**app_settings, "cleanupLevel": CLEANUP_LEVELS[args.cleanup_level],
                    "pluginEnabledState": {**app_settings["pluginEnabledState"], "com.typewhisper.filler-words": False}}
        with isolated_app.IsolatedApp(workdir, configuration=args.configuration, settings=settings,
                                      plugin_settings={"com.typewhisper.openai-compatible": plugin}) as app:
            provider_wait = wait_for_cleanup_provider(app)
            warmup_requests = len(llm.requests)
            for case in cases:
                started = time.perf_counter()
                try:
                    response = app.api("/v1/text/process", {"text": case["input"], "language": case.get("language")})
                    error = None
                except urllib.error.HTTPError as failure:
                    response, error = None, f"HTTP {failure.code}: {failure.read().decode(errors='replace')}"
                rows.append(result_row(case, response, error, round((time.perf_counter() - started) * 1000, 1), llm_label))
            rejected = list(llm.rejected_paths)
    out.parent.mkdir(parents=True, exist_ok=True)
    out.write_text("".join(json.dumps(row) + "\n" for row in rows), encoding="utf-8")
    isolated_app.write_json(out.parent / (out.stem + "-run.json"), isolated_app.run_manifest({
        "kind": "text-eval-capture", "cases": str(args.cases), "case_count": len(cases),
        "cleanup_level": args.cleanup_level, "llm": llm_label, "llm_requests": len(llm.requests) - warmup_requests,
        "llm_warmup_requests": warmup_requests, "provider_wait_seconds": round(provider_wait, 2),
        "llm_rejected_paths": rejected,
    }))
    errors = [row["id"] for row in rows if row["status"] == "error"]
    print(json.dumps({"results": str(out), "completed": len(rows) - len(errors), "errors": errors,
                      "llm_requests": len(llm.requests) - warmup_requests, "provider_wait_seconds": round(provider_wait, 2),
                      "llm_rejected_paths": rejected}, indent=2))
    return 1 if errors or rejected else 0


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("--cases", type=Path, default=ROOT / "evals/dictation/cases.jsonl")
    parser.add_argument("--out", type=Path, default=ROOT / f"artifacts/evals/{dt.date.today()}-text-capture/results.jsonl")
    parser.add_argument("--configuration", default="Release", choices=("Debug", "Release"))
    parser.add_argument("--cleanup-level", default="medium", choices=tuple(CLEANUP_LEVELS))
    parser.add_argument("--llm-delay", type=float, default=0.0)
    return run(parser.parse_args())


if __name__ == "__main__":
    sys.exit(main())
