#!/usr/bin/env python3
"""Run a fixed audio manifest through TypeWhisper CLI and score quality and latency."""

from __future__ import annotations

import argparse
import json
import re
import statistics
import subprocess
import sys
import time
from datetime import datetime, timezone
from pathlib import Path
from typing import Any


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--manifest", required=True, type=Path, help="JSON Lines with id, audio, reference, duration_seconds")
    parser.add_argument("--engine", required=True, help="TypeWhisper engine ID")
    parser.add_argument("--model", required=True, help="Model ID")
    parser.add_argument("--expect-backend", required=True, help="Backend reported by /v1/status")
    parser.add_argument("--language", default="en", help="BCP-47 language tag")
    parser.add_argument("--output", required=True, type=Path, help="JSON result artifact path")
    parser.add_argument("--cli", default="typewhisper-cli", help="TypeWhisper CLI executable")
    return parser.parse_args()


def load_manifest(path: Path) -> list[dict[str, Any]]:
    rows: list[dict[str, Any]] = []
    for number, line in enumerate(path.read_text(encoding="utf-8").splitlines(), 1):
        if not line.strip():
            continue
        item = json.loads(line)
        for field in ("id", "audio", "reference", "duration_seconds"):
            if field not in item:
                raise ValueError(f"Manifest line {number} is missing {field!r}")
        audio = Path(item["audio"])
        if not audio.is_absolute():
            audio = (path.parent / audio).resolve()
        if not audio.is_file():
            raise FileNotFoundError(f"Audio for {item['id']} was not found: {audio}")
        item["audio_path"] = audio
        rows.append(item)
    if not rows:
        raise ValueError("Manifest has no audio rows")
    return rows


def run_cli(cli: str, arguments: list[str], timeout: int = 300) -> subprocess.CompletedProcess[str]:
    return subprocess.run([cli, *arguments], capture_output=True, text=True, timeout=timeout, check=False)


def read_active_backend(cli: str) -> tuple[str | None, bool | None, str | None]:
    response = run_cli(cli, ["status", "--json"], timeout=10)
    if response.returncode != 0:
        raise RuntimeError(f"Could not read app status: {response.stderr.strip() or response.stdout.strip()}")
    try:
        payload = json.loads(response.stdout)
    except json.JSONDecodeError as error:
        raise RuntimeError("The app status command did not return JSON") from error
    acceleration = payload.get("acceleration") or {}
    return acceleration.get("active_backend"), acceleration.get("requires_restart"), payload.get("model")


def normalize(text: str) -> list[str]:
    cleaned = "".join(char.lower() if char.isalnum() else " " for char in text)
    return re.sub(r"\s+", " ", cleaned).split()


def edit_distance(reference: list[str], hypothesis: list[str]) -> int:
    previous = list(range(len(hypothesis) + 1))
    for row, ref_token in enumerate(reference, 1):
        current = [row]
        for column, hyp_token in enumerate(hypothesis, 1):
            current.append(min(
                current[-1] + 1,
                previous[column] + 1,
                previous[column - 1] + (ref_token != hyp_token),
            ))
        previous = current
    return previous[-1]


def parse_transcript(stdout: str) -> dict[str, Any]:
    try:
        payload = json.loads(stdout)
    except json.JSONDecodeError as error:
        raise RuntimeError("Transcription did not return JSON") from error
    if not isinstance(payload, dict) or not isinstance(payload.get("text"), str):
        raise RuntimeError("Transcription JSON has no text field")
    return payload


def run() -> int:
    args = parse_args()
    items = load_manifest(args.manifest)
    result: dict[str, Any] = {
        "started_utc": datetime.now(timezone.utc).isoformat(),
        "commit": subprocess.run(["git", "rev-parse", "HEAD"], capture_output=True, text=True, check=False).stdout.strip(),
        "engine": args.engine,
        "model": args.model,
        "expected_backend": args.expect_backend,
        "language": args.language,
        "manifest": str(args.manifest.resolve()),
        "sample_count": len(items),
        "rows": [],
    }

    try:
        active_backend, requires_restart, active_model = read_active_backend(args.cli)
        result["observed_backend"] = active_backend
        result["requires_restart"] = requires_restart
        result["active_model_before_run"] = active_model
        if active_backend != args.expect_backend:
            raise RuntimeError(f"Expected backend {args.expect_backend!r}, app reports {active_backend!r}")
        if requires_restart:
            raise RuntimeError("App reports that an acceleration restart is required")

        for item in items:
            started = time.perf_counter()
            completed = run_cli(args.cli, [
                "transcribe", str(item["audio_path"]),
                "--language", args.language,
                "--engine", args.engine,
                "--model", args.model,
                "--json", "--no-corrections",
            ])
            elapsed = time.perf_counter() - started
            if completed.returncode != 0:
                raise RuntimeError(
                    f"Transcription failed for {item['id']} with exit {completed.returncode}: "
                    f"{completed.stderr.strip() or completed.stdout.strip()}"
                )
            response = parse_transcript(completed.stdout)
            reference = normalize(str(item["reference"]))
            hypothesis = normalize(response["text"])
            reference_chars = list("".join(reference))
            hypothesis_chars = list("".join(hypothesis))
            row = {
                "id": str(item["id"]),
                "duration_seconds": float(item["duration_seconds"]),
                "complete_response_seconds": elapsed,
                "reference": str(item["reference"]),
                "hypothesis": response["text"],
                "word_errors": edit_distance(reference, hypothesis),
                "reference_words": len(reference),
                "char_errors": edit_distance(reference_chars, hypothesis_chars),
                "reference_chars": len(reference_chars),
                "engine": response.get("engine"),
                "model": response.get("model"),
            }
            result["rows"].append(row)
            print(json.dumps({"event": "transcribed", "id": row["id"], "seconds": round(elapsed, 4)}), flush=True)
    except Exception as error:  # Keep completed rows when a later item fails.
        result["error"] = str(error)
    finally:
        rows = result["rows"]
        latencies = [row["complete_response_seconds"] for row in rows]
        total_words = sum(row["reference_words"] for row in rows)
        total_chars = sum(row["reference_chars"] for row in rows)
        audio_seconds = sum(row["duration_seconds"] for row in rows)
        if latencies:
            warm = sorted(latencies[1:])
            result.update({
                "completed_count": len(rows),
                "first_result_seconds": latencies[0],
                "warm_median_seconds": statistics.median(warm) if warm else None,
                "warm_p95_seconds": warm[int(0.95 * (len(warm) - 1))] if warm else None,
                "total_complete_response_seconds": sum(latencies),
                "audio_seconds": audio_seconds,
                "aggregate_rtf": sum(latencies) / audio_seconds if audio_seconds else None,
                "rtf_note": "Includes a new CLI process and local API request for each recording. Compare runs made with this same helper.",
                "wer": sum(row["word_errors"] for row in rows) / total_words if total_words else None,
                "cer": sum(row["char_errors"] for row in rows) / total_chars if total_chars else None,
                "latency_note": "Complete-response time. The first request can include model loading. This is not streaming first-token time.",
            })
        args.output.parent.mkdir(parents=True, exist_ok=True)
        args.output.write_text(json.dumps(result, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
        print(f"results={args.output}", file=sys.stderr)

    return 0 if "error" not in result and len(result["rows"]) == len(items) else 2


if __name__ == "__main__":
    sys.exit(run())
