#!/usr/bin/env python3
"""Release app, real Sherpa ASR, local HTTP cleanup provider, private X11 clipboard.

Extract the verification helpers from 4f3aa26d before running. See commands.sh.
The startup hook records events and traces without changing app behavior.
"""
from __future__ import annotations

import argparse
import hashlib
import json
import os
import re
import subprocess
import sys
import time
from pathlib import Path

ROOT = Path(__file__).resolve().parents[3]
BASE = Path(__file__).resolve().parent
TOOL_ROOT = BASE / "tools"
sys.path.insert(0, str((TOOL_ROOT if TOOL_ROOT.exists() else ROOT) / "scripts/verification"))
import hidden_desktop
import isolated_app
from recovery_smoke import X11Clipboard, private_x11_env

isolated_app.ROOT = ROOT
PUBLIC = Path("/tmp/typewhisper-benchmark/1462-170145-0012.wav")


def events(path):
    return [json.loads(line) for line in path.read_text().splitlines()] if path.exists() else []


def synthesize(path, text):
    subprocess.run(["flite", "-voice", "slt", "-t", text, "-o", str(path)], check=True)


def with_poll_energy(audio, target):
    # The existing preview poll requires current energy. A quiet sine after the
    # speech lets it poll the complete acoustic sentence, without more words.
    subprocess.run(["ffmpeg", "-hide_banner", "-loglevel", "error", "-y", "-i", str(audio),
        "-f", "lavfi", "-i", "sine=frequency=1000:duration=6:sample_rate=16000",
        "-filter_complex", "[0:a]aresample=16000[a];[1:a]volume=0.4[b];[a][b]concat=n=2:v=0:a=1[out]",
        "-map", "[out]", "-ac", "1", str(target)], check=True)
    return target


def click_control(desktop, name, case, app):
    snapshot = desktop.accessibility()
    isolated_app.write_json(case / (name.replace(" ", "-") + "-before.json"), snapshot)
    node = hidden_desktop.find_node(snapshot["tree"], name)
    assert node is not None, (name, sorted(hidden_desktop.all_names(snapshot["tree"])))
    # This upstream base names the navigation label, while its parent owns the
    # button action. Activate the nearest actionable ancestor through AT-SPI.
    finder = '''def find(node, name, depth=0, parents=None):
    parents = parents or []
    if node is None or depth > 40: return None
    try:
        if node.get_name() == name:
            for candidate in [node] + list(reversed(parents)):
                action = candidate.get_action_iface()
                if action is not None and action.get_n_actions() > 0: return candidate
        for i in range(node.get_child_count()):
            hit = find(node.get_child_at_index(i), name, depth + 1, parents + [node])
            if hit: return hit
    except Exception: pass
    return None
clicked = None'''
    script = re.sub(r"def find\(node, name, depth=0\):.*?clicked = None", finder,
                    hidden_desktop._ATSPI_SCRIPT, flags=re.S)
    script = script.replace("            action = hit.get_action_iface()", """            component = hit.get_component_iface()
            if component is not None:
                try: component.scroll_to(Atspi.ScrollType.ANYWHERE)
                except Exception: pass
                component.grab_focus()
            action = hit.get_action_iface()""")
    done = subprocess.run([desktop._gi_python(), "-c", script, name],
        env={**os.environ, "DBUS_SESSION_BUS_ADDRESS": desktop.bus_address},
        capture_output=True, text=True, check=True, timeout=30)
    clicked = json.loads(done.stdout)["clicked"]
    assert clicked and clicked["ok"], clicked


def run_case(desktop, name, enabled, miss, audio, suffix, out):
    case = out / name
    case.mkdir(parents=True, exist_ok=True)
    observed = case / "events.jsonl"
    with isolated_app.FakeLlm(delay_seconds=.25) as llm:
        settings, plugin = llm.plugin_overrides()
        settings.update({
            "selectedModelId": "plugin:com.typewhisper.sherpa-onnx:parakeet-tdt-0.6b",
            "liveTranscriptionEnabled": True, "liveCleanupEnabled": enabled,
            "cleanupLevel": 2, "saveToHistoryEnabled": True,
            "captureLlmProvenance": True, "transcriptionNumberNormalizationEnabled": True,
            "spokenFormattingStrategy": 2,
            "pluginEnabledState": {"com.typewhisper.openai-compatible": True,
                                   "com.typewhisper.filler-words": False},
        })
        display_env = private_x11_env(desktop.app_env())
        # A private desktop is outside logind. This private bus deliberately has no
        # login1 service. The app takes its supported non-systemd path. The host
        # login session and its lock state remain untouched.
        display_env.update(DBUS_SYSTEM_BUS_ADDRESS=desktop.bus_address,
                           DOTNET_STARTUP_HOOKS=str(BASE / "observer/bin/Release/net10.0/Observer.dll"),
                           TW_OBSERVER_PATH=str(observed))
        with isolated_app.IsolatedApp(case, configuration="Release", settings=settings,
                plugin_data=["com.typewhisper.sherpa-onnx"],
                plugin_settings={"com.typewhisper.openai-compatible": plugin},
                virtual_mic=True, display="none", extra_env=display_env, minimized=False) as app:
            app.load_model("sherpa-onnx", "parakeet-tdt-0.6b")
            if "ui" in name:
                click_control(desktop, "Dictation", case, app)
                time.sleep(.5)
                tree = desktop.accessibility()["tree"]
                isolated_app.write_json(case / "dictation-controls.json", tree)
                names = hidden_desktop.all_names(tree)
                assert "Clean drafts while recording" in names, sorted(names)
                assert not any("wait limit" in text.lower() for text in names)
                desktop.screenshot(case / "dictation-controls.png")
                settings_path = app.instance / "data/TypeWhisper/settings.json"
                for expected in (not enabled, enabled):
                    click_control(desktop, "Clean drafts while recording", case, app)
                    deadline = time.monotonic() + 5
                    while json.loads(settings_path.read_text())["liveCleanupEnabled"] != expected:
                        assert time.monotonic() < deadline, expected
                        time.sleep(.05)
                assert not llm.requests, llm.requests
                time.sleep(.3)
                desktop.screenshot(case / "dictation-controls-after-toggle.png")
            deadline = time.monotonic() + 15
            while not any(e["kind"] == "observer" for e in events(observed)):
                assert time.monotonic() < deadline, "observer not attached"
                time.sleep(.1)
            clip = X11Clipboard(app.env)
            clip.write("private clipboard sentinel")
            started = app.api("/v1/dictation/start", {})
            time.sleep(.3)
            sources = app.capture_sources()
            assert sources and all(s == app.mic.source for s in sources), sources
            app.mic.play(audio)
            time.sleep(6)
            before = len(llm.requests)
            assert (before > 0) == enabled, (name, before)
            desktop.screenshot(case / "recording.png")
            if miss:
                app.mic.play(suffix)
            stop_at = time.time()
            stopped = app.api("/v1/dictation/stop", {})
            path = f"/v1/dictation/transcription?sessionId={started['session_id']}"
            deadline = time.monotonic() + 30
            while True:
                result = app.api(path)
                if result.get("state") == "ready":
                    break
                assert time.monotonic() < deadline, result
                time.sleep(.05)
            deadline = time.monotonic() + 10
            while True:
                delivered = clip.read().decode()
                if delivered.strip() == result["text"].strip():
                    break
                assert time.monotonic() < deadline, (delivered, result)
                time.sleep(.02)
            delivered_at = time.time()
            time.sleep(.3)
            history = app.api("/v1/history")
            status = app.api("/v1/status")
            desktop.screenshot(case / "delivered.png")
            ev = events(observed)
            reused = any("Reused matching draft" in e["text"] for e in ev)
            isolated_app.write_json(case / "observed-before-assertions.json", {
                "result": result, "history": history, "clipboard": delivered,
                "llm_requests": llm.requests, "reused": reused})
            assert reused == (enabled and not miss), (name, reused, llm.requests)
            after = [r for r in llm.requests if r["at"] >= stop_at]
            assert len(after) == (0 if reused else 1), (name, after)
            if not enabled:
                assert len(llm.requests) == 1, llm.requests
            final_event = next(e for e in ev if e["kind"] == "final_text")
            transcribe = next(e for e in ev if e["kind"] == "status" and e["text"].startswith("Transcribing"))
            cleanup_start = (next(e["at"] for e in ev if "Reused matching draft" in e["text"])
                             if reused else after[0]["at"])
            inserting = next(e for e in ev if e["kind"] == "status" and e["text"].startswith("Inserting"))
            record = {
                "name": name, "enabled": enabled, "miss": miss, "audio": str(audio),
                "fixture_kind": "public LibriSpeech plus non-speech poll energy cue" if name.startswith("public") else "synthetic Flite slt plus non-speech poll energy cue",
                "audio_sha256": hashlib.sha256(audio.read_bytes()).hexdigest(),
                "settings": settings, "started": started, "stopped": stopped, "result": result,
                "history": history, "capture_sources": sources,
                "clipboard_text": delivered, "clipboard_readback_matches": True,
                "llm_requests": llm.requests, "draft_calls": before, "final_calls": len(after),
                "reused": reused, "rejected_paths": llm.rejected_paths,
                "acceleration": status.get("acceleration"),
                "timings_ms": {
                    "transcription_and_precleanup_upper_bound": round((cleanup_start-transcribe["at"])*1000, 2),
                    "cleanup_to_final_event_upper_bound": round((final_event["at"]-cleanup_start)*1000, 2),
                    "delivery_observed_upper_bound": round((delivered_at-inserting["at"])*1000, 2),
                    "stop_to_clipboard_readback": round((delivered_at-stop_at)*1000, 2),
                },
                "timing_limits": "Observer event boundaries. ASR includes precleanup; cleanup includes later local steps; delivery includes polling/readback. No performance DTO fields imported.",
            }
            if name.startswith("spoken"):
                prompts = [json.loads(r["prompt"][r["prompt"].index("{"):])["dictated_text"] for r in llm.requests]
                assert any("?" in text and "23" in text for text in prompts), prompts
                assert all("question mark" not in text.lower() for text in prompts), prompts
                record["normalized_spoken_punctuation_and_number"] = True
            isolated_app.write_json(case / "result.json", record)
            isolated_app.write_json(case / "run.json", isolated_app.run_manifest({
                "kind": "pr141-live", "configuration": "Release", "enabled": enabled,
                "miss": miss, "backend": status.get("acceleration"), "audio": str(audio),
                "provider": "controlled local HTTP echo through bundled OpenAI-compatible plugin",
            }))
            print(json.dumps({"case": name, "draft": before, "final": len(after), "reused": reused,
                              "text": result["text"], "timings": record["timings_ms"]}), flush=True)
            return record


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--out", type=Path, default=BASE / "live")
    parser.add_argument("--cases", default="public-off,public-hit,public-miss,spoken-hit,spoken-miss")
    args = parser.parse_args()
    args.out = args.out.resolve()
    args.out.mkdir(parents=True, exist_ok=True)
    synth = args.out / "spoken.wav"
    suffix = args.out / "suffix.wav"
    synthesize(synth, "Can you send twenty three files question mark")
    synthesize(suffix, "Please send it next Tuesday instead.")
    public_fixture = with_poll_energy(PUBLIC, args.out / "public-poll-fixture.wav")
    spoken_fixture = with_poll_energy(synth, args.out / "spoken-poll-fixture.wav")
    results = []
    with hidden_desktop.HiddenDesktop(args.out / "desktop") as desktop:
        for name in args.cases.split(","):
            results.append(run_case(desktop, name, not name.endswith("off"), name.endswith("miss"),
                                    public_fixture if name.startswith("public") else spoken_fixture, suffix, args.out))
    isolated_app.write_json(args.out / "summary.json", [{k: r[k] for k in
        ("name", "draft_calls", "final_calls", "reused", "timings_ms", "acceleration")} for r in results])


if __name__ == "__main__":
    main()
