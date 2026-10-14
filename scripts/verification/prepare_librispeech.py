#!/usr/bin/env python3
"""Prepare a deterministic 50-clip LibriSpeech manifest for ASR comparisons."""

from __future__ import annotations

import argparse
import json
import random
import subprocess
import sys
import tarfile
from pathlib import Path
from urllib.request import urlretrieve


SPLITS = ("test-clean", "test-other")
ARCHIVE_URL = "https://www.openslr.org/resources/12/{split}.tar.gz"


def duration_seconds(path: Path) -> float:
    result = subprocess.run(
        ["ffprobe", "-v", "error", "-show_entries", "format=duration", "-of", "default=nw=1:nk=1", str(path)],
        capture_output=True,
        text=True,
        timeout=30,
        check=True,
    )
    return float(result.stdout.strip())


def ensure_archive(directory: Path, split: str) -> Path:
    archive = directory / f"{split}.tar.gz"
    if not archive.is_file():
        directory.mkdir(parents=True, exist_ok=True)
        print(f"Downloading {ARCHIVE_URL.format(split=split)}", file=sys.stderr)
        urlretrieve(ARCHIVE_URL.format(split=split), archive)
    return archive


def transcript_rows(archive: Path, split: str) -> tuple[dict[str, dict[str, str]], dict[str, list[str]]]:
    rows: dict[str, dict[str, str]] = {}
    by_speaker: dict[str, list[str]] = {}
    with tarfile.open(archive, "r:gz") as tar:
        for member in tar:
            if not member.isfile() or not member.name.endswith(".trans.txt"):
                continue
            stream = tar.extractfile(member)
            if stream is None:
                continue
            for line in stream.read().decode("utf-8").splitlines():
                uid, reference = line.split(" ", 1)
                speaker = uid.split("-", 1)[0]
                rows[uid] = {"id": uid, "split": split, "speaker": speaker, "reference": reference}
                by_speaker.setdefault(speaker, []).append(uid)
    return rows, by_speaker


def prepare(args: argparse.Namespace) -> list[dict[str, object]]:
    rng = random.Random(args.seed)
    selected_rows: list[dict[str, object]] = []
    output_root = args.output.parent
    audio_root = output_root / "librispeech-audio"

    for split in SPLITS:
        archive = ensure_archive(args.archive_dir, split)
        rows, by_speaker = transcript_rows(archive, split)
        speakers = sorted(by_speaker)
        rng.shuffle(speakers)
        chosen_speakers = speakers[: args.speakers_per_split]
        candidate_owner: dict[str, str] = {}
        for speaker in chosen_speakers:
            candidates = list(by_speaker[speaker])
            rng.shuffle(candidates)
            for uid in candidates:
                candidate_owner[f"LibriSpeech/{split}/{speaker}/{rows[uid]['id'].split('-')[1]}/{uid}.flac"] = speaker

        found_speakers: set[str] = set()
        with tarfile.open(archive, "r:gz") as tar:
            for member in tar:
                speaker = candidate_owner.get(member.name)
                if speaker is None or speaker in found_speakers or not member.isfile():
                    continue
                stream = tar.extractfile(member)
                if stream is None:
                    continue
                destination = audio_root / split / Path(member.name).name
                destination.parent.mkdir(parents=True, exist_ok=True)
                with destination.open("wb") as output:
                    output.write(stream.read())
                try:
                    duration = duration_seconds(destination)
                except (OSError, subprocess.SubprocessError, ValueError):
                    destination.unlink(missing_ok=True)
                    continue
                if not 3.0 <= duration <= 20.0:
                    destination.unlink(missing_ok=True)
                    continue
                row = rows[destination.stem]
                selected_rows.append({
                    "id": row["id"],
                    "audio": destination.relative_to(output_root).as_posix(),
                    "reference": row["reference"],
                    "duration_seconds": duration,
                    "split": split,
                    "speaker": speaker,
                })
                found_speakers.add(speaker)
                if len(found_speakers) == len(chosen_speakers):
                    break
        if len(found_speakers) != args.speakers_per_split:
            raise RuntimeError(f"Found {len(found_speakers)} eligible speakers in {split}; expected {args.speakers_per_split}")

    selected_rows.sort(key=lambda item: (str(item["split"]), str(item["speaker"]), str(item["id"])))
    args.output.parent.mkdir(parents=True, exist_ok=True)
    with args.output.open("w", encoding="utf-8") as manifest:
        for row in selected_rows:
            manifest.write(json.dumps(row, ensure_ascii=False) + "\n")
    return selected_rows


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--archive-dir", type=Path, default=Path("benchmarks/data/downloads"))
    parser.add_argument("--output", type=Path, default=Path("benchmarks/data/librispeech-50.jsonl"))
    parser.add_argument("--speakers-per-split", type=int, default=25)
    parser.add_argument("--seed", type=int, default=1913)
    args = parser.parse_args()
    try:
        rows = prepare(args)
    except (OSError, RuntimeError, tarfile.TarError, subprocess.SubprocessError) as error:
        print(f"benchmark corpus preparation failed: {error}", file=sys.stderr)
        return 2
    print(f"Prepared {len(rows)} recordings in {args.output}")
    print(f"Audio files are under {args.output.parent / 'librispeech-audio'}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
