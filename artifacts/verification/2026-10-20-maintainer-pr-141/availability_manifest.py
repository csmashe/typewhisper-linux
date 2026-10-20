#!/usr/bin/env python3
"""Record source, assembly and isolated-run evidence after verification."""
import hashlib
import json
import subprocess
from pathlib import Path

root = Path(__file__).resolve().parents[3]
base = Path(__file__).resolve().parent


def git(*args):
    return subprocess.check_output(["git", "-C", str(root), *args], text=True).strip()


def load(path):
    return json.loads((base / path).read_text())


source = git("rev-parse", "HEAD")
assert source == "f2c93c3e9cbf669f33dc73c77a034aa869fa5637"
assert not git("status", "--porcelain")
runs = {name: load(path) for name, path in {
    "ui": "availability-ui-clean/run.json",
    "audio": "availability-audio-clean/spoken-hit/run.json",
}.items()}
assert all(run["commit"] == source and run["dirty_files"] == [] for run in runs.values())
before = load("availability-doctor-before.json")
after = load("availability-doctor-final.json")
modules = subprocess.check_output(["pactl", "list", "short", "modules"], text=True)
cleanup = {
    "null_sink_modules": [line for line in modules.splitlines() if "module-null-sink" in line],
    "verification_runtime_directories": [str(path) for path in Path("/tmp").glob("tw-verify-*")],
    "user_processes_before": before["app"]["processes"],
    "user_processes_after": after["app"]["processes"],
    "user_control_status_after": after["app"]["control_status"],
}
assert not cleanup["null_sink_modules"] and not cleanup["verification_runtime_directories"]
assert cleanup["user_processes_before"] == cleanup["user_processes_after"]
assert cleanup["user_control_status_after"]["state"] == "idle"
files = ["src/TypeWhisper.Linux/bin/Release/net10.0/typewhisper.dll",
         "src/TypeWhisper.Core/bin/Release/net10.0/TypeWhisper.Core.dll",
         "src/TypeWhisper.Linux/bin/Release/net10.0/Plugins/com.typewhisper.openai-compatible/TypeWhisper.Plugin.OpenAiCompatible.dll",
         "src/TypeWhisper.Linux/bin/Release/net10.0/Plugins/com.typewhisper.filler-words/TypeWhisper.Plugin.FillerWords.dll"]
manifest = {
    "source_commit": source,
    "original_lease": "415e91d3f988657180f705b7459f49135b06c78d",
    "parent_commit": "bb38d2ee6fd59f48ca3cf50719792910af7be26d",
    "prior_tested_source": "aa27dc55bc52a0963291ffe6aa9eb31b68fd187d",
    "git_status_porcelain": "",
    "followup_changed_files": git("diff", "--name-only", "bb38d2ee", "HEAD").splitlines(),
    "release_sha256": {path: hashlib.sha256((root / path).read_bytes()).hexdigest() for path in files},
    "assembly_informational_version": next(line for line in
        (root / "src/TypeWhisper.Linux/obj/Release/net10.0/TypeWhisper.Linux.AssemblyInfo.cs").read_text().splitlines()
        if "AssemblyInformationalVersionAttribute" in line),
    "runs": runs,
    "ui_result": load("availability-ui-clean/result.json"),
    "audio_summary": load("availability-audio-clean/summary.json"),
    "cleanup": cleanup,
}
assert source in manifest["assembly_informational_version"]
(base / "availability-clean-manifest.json").write_text(json.dumps(manifest, indent=2) + "\n")
