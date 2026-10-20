#!/usr/bin/env python3
"""Exercise the changed settings control in its own private X11 desktop."""
import json
import argparse
import time
from pathlib import Path
from live_verify import hidden_desktop, isolated_app, private_x11_env, click_control

parser = argparse.ArgumentParser()
parser.add_argument("--out", type=Path, default=Path(__file__).resolve().parent / "ui-controls-final")
out = parser.parse_args().out.resolve()
out.mkdir(parents=True, exist_ok=True)
with hidden_desktop.HiddenDesktop(out / "desktop") as desktop, isolated_app.FakeLlm() as llm:
    settings, plugin = llm.plugin_overrides()
    settings.update(cleanupLevel=2, liveCleanupEnabled=False)
    env = private_x11_env(desktop.app_env())
    env["DBUS_SYSTEM_BUS_ADDRESS"] = desktop.bus_address
    with isolated_app.IsolatedApp(out, configuration="Release", settings=settings, minimized=False,
            display="none", extra_env=env,
            plugin_settings={"com.typewhisper.openai-compatible": plugin}) as app:
        click_control(desktop, "Dictation", out, app)
        time.sleep(.5)
        names = hidden_desktop.all_names(desktop.accessibility()["tree"])
        assert "Clean drafts while recording" in names
        assert not any("wait limit" in name.lower() for name in names)
        saved = app.instance / "data/TypeWhisper/settings.json"
        states = []
        for expected in (True, False):
            click_control(desktop, "Clean drafts while recording", out, app)
            time.sleep(.5)
            actual = json.loads(saved.read_text())["liveCleanupEnabled"]
            assert actual == expected, (actual, expected)
            states.append(actual)
            desktop.screenshot(out / ("enabled.png" if expected else "disabled.png"))
        assert not llm.requests
        isolated_app.write_json(out / "result.json", {
            "persisted_states": states, "llm_calls": len(llm.requests),
            "timeout_control_absent": True, "private_x11": True})
        isolated_app.write_json(out / "run.json", isolated_app.run_manifest({
            "kind": "pr141-settings", "configuration": "Release", "private_x11": True}))
