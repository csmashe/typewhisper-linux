#!/usr/bin/env python3
"""Verify default plugin blocking and saved preference through the real UI."""
import argparse
import json
import os
import subprocess
import time
from pathlib import Path

from live_verify import hidden_desktop, isolated_app, private_x11_env, click_control


def scroll_to_control(desktop, name):
    snapshot = desktop.accessibility()
    node = hidden_desktop.find_node(snapshot["tree"], name)
    assert node, name
    # Use the visible scrollbar's accessibility value. Avalonia's AT-SPI
    # scroll_to implementation does not move this ScrollViewer.
    delta = node["extents"][1] - 450
    script = r'''
import gi, sys, json
gi.require_version("Atspi", "2.0")
from gi.repository import Atspi
def walk(node, depth=0):
    if node is None or depth > 40: return
    if node.get_role_name() == "scroll bar":
        box = node.get_component_iface().get_extents(Atspi.CoordType.SCREEN)
        if box.x > 1000:
            value = node.get_value_iface()
            before = value.get_current_value()
            after = min(value.get_maximum_value(), max(value.get_minimum_value(), before + float(sys.argv[1])))
            assert value.set_current_value(after)
            print(json.dumps({"before": before, "after": after}))
            return True
    for i in range(node.get_child_count()):
        if walk(node.get_child_at_index(i), depth + 1): return True
assert walk(Atspi.get_desktop(0)), "dictation scrollbar absent"
'''
    subprocess.run([desktop._gi_python(), "-c", script, str(delta)],
                   env={**os.environ, "DBUS_SESSION_BUS_ADDRESS": desktop.bus_address},
                   capture_output=True, text=True, check=True, timeout=30)
    time.sleep(.5)
    node = hidden_desktop.find_node(desktop.accessibility()["tree"], name)
    assert 160 <= node["extents"][1] <= 750, node


def inspect_control(desktop, name, toggle_plugin=False):
    script = r'''
import gi, json, sys
gi.require_version("Atspi", "2.0")
from gi.repository import Atspi
def descendants(node, depth=0):
    if node is None or depth > 40: return
    yield node
    for i in range(node.get_child_count()):
        yield from descendants(node.get_child_at_index(i), depth + 1)
def find(node, parents=None, depth=0):
    if node is None or depth > 40: return None
    parents = parents or []
    if node.get_name() == sys.argv[1]: return node, parents
    for i in range(node.get_child_count()):
        hit = find(node.get_child_at_index(i), parents + [node], depth + 1)
        if hit: return hit
    return None
hit = find(Atspi.get_desktop(0))
assert hit, sys.argv[1]
node, parents = hit
if sys.argv[2] == "toggle":
    target = None
    for parent in reversed(parents):
        toggles = [n for n in descendants(parent) if n.get_role_name() in ("toggle button", "check box")]
        if toggles:
            assert len(toggles) == 1, [(n.get_name(), n.get_role_name()) for n in toggles]
            target = toggles[0]
            break
    assert target is not None, "plugin toggle absent"
    node = target
component = node.get_component_iface()
if component is not None:
    try: component.scroll_to(Atspi.ScrollType.ANYWHERE)
    except Exception: pass
    component.grab_focus()
state = node.get_state_set()
result = {"name": node.get_name(), "role": node.get_role_name(),
          "enabled": state.contains(Atspi.StateType.ENABLED),
          "sensitive": state.contains(Atspi.StateType.SENSITIVE),
          "checked": state.contains(Atspi.StateType.CHECKED)}
if sys.argv[2] == "toggle":
    action = node.get_action_iface()
    assert action is not None
    result["clicked"] = bool(action.do_action(0))
    assert result["clicked"]
print(json.dumps(result))
'''
    done = subprocess.run([desktop._gi_python(), "-c", script, name,
                           "toggle" if toggle_plugin else "inspect"],
                          env={**os.environ, "DBUS_SESSION_BUS_ADDRESS": desktop.bus_address},
                          capture_output=True, text=True, check=True, timeout=30)
    return json.loads(done.stdout)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--out", required=True, type=Path)
    out = parser.parse_args().out.resolve()
    out.mkdir(parents=True, exist_ok=True)
    label = "Clean drafts while recording"
    with hidden_desktop.HiddenDesktop(out / "desktop") as desktop, isolated_app.FakeLlm() as llm:
        settings, plugin = llm.plugin_overrides()
        settings.update(cleanupLevel=2, liveCleanupEnabled=True)
        env = private_x11_env(desktop.app_env())
        env["DBUS_SYSTEM_BUS_ADDRESS"] = desktop.bus_address
        with isolated_app.IsolatedApp(out, configuration="Release", settings=settings,
                minimized=False, display="none", extra_env=env,
                virtual_mic=True,
                plugin_settings={"com.typewhisper.openai-compatible": plugin}) as app:
            saved = app.instance / "data/TypeWhisper/settings.json"
            click_control(desktop, "Dictation", out, app)
            time.sleep(.5)
            blocked = inspect_control(desktop, label)
            tree = desktop.accessibility()
            isolated_app.write_json(out / "blocked-tree.json", tree)
            names = hidden_desktop.all_names(tree["tree"])
            reason = next(n for n in names if n.startswith("Live cleanup is unavailable"))
            assert "Filler Words" in reason and "Disable them in Plugins" in reason, reason
            assert not blocked["enabled"] and not blocked["sensitive"], blocked
            assert blocked["checked"], blocked
            assert json.loads(saved.read_text())["liveCleanupEnabled"] is True
            scroll_to_control(desktop, label)
            desktop.screenshot(out / "blocked.png")

            click_control(desktop, "Plugins", out, app)
            time.sleep(.5)
            isolated_app.write_json(out / "plugins-before.json", desktop.accessibility())
            toggle = inspect_control(desktop, "Filler Words", toggle_plugin=True)
            time.sleep(.7)
            state = json.loads(saved.read_text())
            assert state["pluginEnabledState"]["com.typewhisper.filler-words"] is False
            assert state["liveCleanupEnabled"] is True
            desktop.screenshot(out / "plugin-disabled.png")
            click_control(desktop, "Dictation", out, app)
            time.sleep(.5)
            available = inspect_control(desktop, label)
            assert available["enabled"] and available["sensitive"] and available["checked"], available
            names = hidden_desktop.all_names(desktop.accessibility()["tree"])
            assert not any(n.startswith("Live cleanup is unavailable") for n in names)
            scroll_to_control(desktop, label)
            desktop.screenshot(out / "available.png")
            states = []
            for expected in (False, True):
                click_control(desktop, label, out, app)
                time.sleep(.3)
                actual = json.loads(saved.read_text())["liveCleanupEnabled"]
                assert actual == expected, (actual, expected)
                states.append(actual)
            assert not llm.requests
            isolated_app.write_json(out / "result.json", {
                "blocked": blocked, "reason": reason, "plugin_toggle": toggle,
                "available": available, "persisted_states": states,
                "preference_preserved_across_availability": True, "llm_calls": 0})
            isolated_app.write_json(out / "run.json", isolated_app.run_manifest({
                "kind": "pr141-availability-ui", "configuration": "Release", "private_x11": True}))


if __name__ == "__main__":
    main()
