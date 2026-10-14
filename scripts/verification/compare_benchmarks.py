#!/usr/bin/env python3
"""Compare complete-response latency and WER for two runs of the same audio corpus."""
import argparse
import json
import math
from pathlib import Path
import statistics


def summary(report):
    rows = report.get("rows", [])
    if report.get("error") or not rows or report.get("sample_count") != len(rows):
        raise ValueError("Cannot compare an incomplete benchmark")
    latency = [float(row["complete_response_seconds"]) for row in rows]
    if any(not math.isfinite(value) or value <= 0 for value in latency):
        raise ValueError("Latency must be positive and finite")
    warm = sorted(latency[1:])
    words = sum(row["reference_words"] for row in rows)
    return {
        "engine": report.get("engine"), "model": report.get("model"),
        "backend": report.get("observed_backend"), "commit": report.get("commit"),
        "sample_count": len(rows), "first_request_seconds": latency[0],
        "warm_median_seconds": statistics.median(warm) if warm else None,
        "warm_p95_seconds": warm[math.ceil(0.95 * len(warm)) - 1] if warm else None,
        "wer": sum(row["word_errors"] for row in rows) / words if words else None,
    }


def compare(baseline, candidate):
    before, after = summary(baseline), summary(candidate)
    corpus = lambda report: [(row["id"], row["reference"], float(row["duration_seconds"])) for row in report["rows"]]
    if corpus(baseline) != corpus(candidate):
        raise ValueError("Benchmark corpus, references, durations, and order must match")
    ratio = lambda a, b: a / b if a is not None and b is not None else None
    return {
        "baseline": before, "candidate": after,
        "first_request_speedup": ratio(before["first_request_seconds"], after["first_request_seconds"]),
        "warm_median_speedup": ratio(before["warm_median_seconds"], after["warm_median_seconds"]),
        "warm_p95_speedup": ratio(before["warm_p95_seconds"], after["warm_p95_seconds"]),
        "wer_delta": after["wer"] - before["wer"] if before["wer"] is not None and after["wer"] is not None else None,
        "measurement": "Complete CLI/API response, including process and transport overhead. Excludes cleanup and insertion. First request is separated; a cold load is not assumed.",
        "interpretation": "Speedup above 1 is faster. Positive WER delta means more word errors. One run is descriptive, not evidence of a stable performance gain.",
    }


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("baseline", type=Path)
    parser.add_argument("candidate", type=Path)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    result = compare(json.loads(args.baseline.read_text()), json.loads(args.candidate.read_text()))
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(result, indent=2) + "\n")
    print(json.dumps(result, indent=2))


if __name__ == "__main__":
    main()
