#!/usr/bin/env python3
"""A hidden GNOME compositor for live UI checks.

Starts a private D-Bus session, an accessibility bus and registry on it, and
`mutter --headless` with one virtual monitor. Apps started with `app_env()` draw
into that compositor's Xwayland display and publish their AT-SPI tree on the
private bus. Nothing reaches the user's screen, session bus, or input devices.

Do not use ydotool or other uinput tools here: they type into the user's real desktop.
Act through AT-SPI actions instead, and capture frames through mutter's ScreenCast API.
"""

from __future__ import annotations

import json
import os
import re
import secrets
import shutil
import subprocess
import sys
import time
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
import isolated_app  # noqa: E402

A11Y_LAUNCHER = "/usr/lib/at-spi-bus-launcher"
A11Y_REGISTRY = "/usr/lib/at-spi2-registryd"

_ATSPI_SCRIPT = r'''
import gi, json, sys
gi.require_version("Atspi", "2.0")
from gi.repository import Atspi
desktop = Atspi.get_desktop(0)
apps = [desktop.get_child_at_index(i) for i in range(desktop.get_child_count())]
def walk(node, depth):
    if node is None or depth > 40: return None
    try:
        item = {"role": node.get_role_name(), "name": node.get_name() or ""}
        component = node.get_component_iface()
        if component is not None:
            box = component.get_extents(Atspi.CoordType.SCREEN)
            item["extents"] = [box.x, box.y, box.width, box.height]
        text = node.get_text_iface()
        if text is not None:
            item["text"] = text.get_text(0, -1)
        children = [walk(node.get_child_at_index(i), depth + 1) for i in range(min(node.get_child_count(), 200))]
        children = [c for c in children if c]
        if children: item["children"] = children
        return item
    except Exception as error:
        return {"error": type(error).__name__}
def find(node, name, depth=0):
    if node is None or depth > 40: return None
    try:
        if node.get_name() == name and node.get_action_iface() is not None: return node
        for i in range(node.get_child_count()):
            hit = find(node.get_child_at_index(i), name, depth + 1)
            if hit: return hit
    except Exception:
        return None
clicked = None
if len(sys.argv) > 1:
    for app in apps:
        hit = find(app, sys.argv[1])
        if hit:
            action = hit.get_action_iface()
            clicked = {"role": hit.get_role_name(), "action": action.get_action_name(0), "ok": bool(action.do_action(0))}
            break
print(json.dumps({"clicked": clicked, "tree": [walk(app, 0) for app in apps]}))
'''

_SCREENSHOT_SCRIPT = r'''
import gi, sys, subprocess
gi.require_version("Gio", "2.0")
from gi.repository import Gio, GLib
bus = Gio.bus_get_sync(Gio.BusType.SESSION)
def call(path, iface, method, args, rtype):
    return bus.call_sync("org.gnome.Mutter.ScreenCast", path, iface, method, args,
                         GLib.VariantType(rtype) if rtype else None, Gio.DBusCallFlags.NONE, 10000, None)
session = call("/org/gnome/Mutter/ScreenCast", "org.gnome.Mutter.ScreenCast", "CreateSession",
               GLib.Variant("(a{sv})", ({},)), "(o)")[0]
stream = call(session, "org.gnome.Mutter.ScreenCast.Session", "RecordMonitor",
              GLib.Variant("(sa{sv})", ("", {"cursor-mode": GLib.Variant("u", 0)})), "(o)")[0]
loop, node = GLib.MainLoop(), {}
def added(conn, sender, path, iface, signal, params, data):
    node["id"] = params[0]; loop.quit()
bus.signal_subscribe(None, "org.gnome.Mutter.ScreenCast.Stream", "PipeWireStreamAdded", stream, None, 0, added, None)
call(session, "org.gnome.Mutter.ScreenCast.Session", "Start", None, None)
GLib.timeout_add_seconds(10, loop.quit); loop.run()
if "id" not in node: sys.exit("screencast stream did not start")
done = subprocess.run(["gst-launch-1.0", "-q", "pipewiresrc", f"path={node['id']}", "num-buffers=8", "!",
                       "videoconvert", "!", "pngenc", "snapshot=true", "!", "filesink", f"location={sys.argv[1]}"], timeout=20)
call(session, "org.gnome.Mutter.ScreenCast.Session", "Stop", None, None)
sys.exit(done.returncode)
'''


_WATCH_SCRIPT = r'''
import gi, json, sys, time
gi.require_version("Atspi", "2.0")
from gi.repository import Atspi
frame_name, deadline = sys.argv[1], time.time() + float(sys.argv[2])
def texts(node, depth=0, out=None):
    out = [] if out is None else out
    if node is None or depth > 40: return out
    try:
        if node.get_role_name() in ("label", "text", "static") and node.get_name():
            out.append(node.get_name())
        for i in range(node.get_child_count()):
            texts(node.get_child_at_index(i), depth + 1, out)
    except Exception:
        pass
    return out
last = None
while time.time() < deadline:
    desktop = Atspi.get_desktop(0)
    found = []
    for i in range(desktop.get_child_count()):
        app = desktop.get_child_at_index(i)
        if app is None: continue
        app.clear_cache()
        for j in range(app.get_child_count()):
            frame = app.get_child_at_index(j)
            if frame is not None and frame.get_name() == frame_name:
                found.extend(texts(frame))
    if found != last:
        print(json.dumps({"t": time.time(), "texts": found}), flush=True)
        last = found
    time.sleep(0.1)
'''


_KEYSYMS = {"ctrl": 0xffe3, "control": 0xffe3, "shift": 0xffe1, "alt": 0xffe9, "meta": 0xffeb, "super": 0xffeb,
            "escape": 0xff1b, "esc": 0xff1b, "enter": 0xff0d, "return": 0xff0d, "tab": 0xff09, "space": 0x20,
            "backspace": 0xff08, "delete": 0xffff, "left": 0xff51, "up": 0xff52, "right": 0xff53, "down": 0xff54}
_KEYSYMS.update({f"f{n}": 0xffbe + n - 1 for n in range(1, 25)})


def shortcut_keysyms(shortcut: str) -> list[int]:
    """`Ctrl+Shift+F8` to X keysyms, modifiers first, for mutter's NotifyKeyboardKeysym."""
    keysyms = []
    for part in shortcut.split("+"):
        name = part.strip().lower()
        if name in _KEYSYMS:
            keysyms.append(_KEYSYMS[name])
        elif len(name) == 1 and name.isprintable():
            keysyms.append(ord(name))
        else:
            raise ValueError(f"unknown key {part!r} in {shortcut!r}")
    return keysyms


# Pointer and keyboard input through mutter's RemoteDesktop API, linked to a ScreenCast
# stream so absolute pointer coordinates map onto the virtual monitor. Input reaches only
# the hidden compositor. argv: a JSON list of steps such as
# [{"click": [x, y]}, {"keys": [keysym, ...]}, {"wait": 0.3}].
_INPUT_SCRIPT = r'''
import gi, json, sys, time
gi.require_version("Gio", "2.0")
from gi.repository import Gio, GLib
bus = Gio.bus_get_sync(Gio.BusType.SESSION)
def call(name, path, iface, method, args, rtype=None):
    return bus.call_sync(name, path, iface, method, args, GLib.VariantType(rtype) if rtype else None,
                         Gio.DBusCallFlags.NONE, 10000, None)
RD, SC = "org.gnome.Mutter.RemoteDesktop", "org.gnome.Mutter.ScreenCast"
rd = call(RD, "/org/gnome/Mutter/RemoteDesktop", RD, "CreateSession", None, "(o)")[0]
session_id = bus.call_sync(RD, rd, "org.freedesktop.DBus.Properties", "Get",
    GLib.Variant("(ss)", (RD + ".Session", "SessionId")), GLib.VariantType("(v)"), 0, 10000, None)[0]
sc = call(SC, "/org/gnome/Mutter/ScreenCast", SC, "CreateSession",
          GLib.Variant("(a{sv})", ({"remote-desktop-session-id": GLib.Variant("s", session_id)},)), "(o)")[0]
stream = call(SC, sc, SC + ".Session", "RecordMonitor", GLib.Variant("(sa{sv})", ("", {})), "(o)")[0]
call(RD, rd, RD + ".Session", "Start", None)
time.sleep(0.5)
for step in json.loads(sys.argv[1]):
    if "click" in step:
        x, y = step["click"]
        call(RD, rd, RD + ".Session", "NotifyPointerMotionAbsolute", GLib.Variant("(sdd)", (stream, float(x), float(y))))
        time.sleep(0.05)
        for pressed in (True, False):
            call(RD, rd, RD + ".Session", "NotifyPointerButton", GLib.Variant("(ib)", (0x110, pressed)))
            time.sleep(0.05)
    if "keys" in step:
        for keysym in step["keys"]:
            call(RD, rd, RD + ".Session", "NotifyKeyboardKeysym", GLib.Variant("(ub)", (keysym, True)))
            time.sleep(0.03)
        for keysym in reversed(step["keys"]):
            call(RD, rd, RD + ".Session", "NotifyKeyboardKeysym", GLib.Variant("(ub)", (keysym, False)))
            time.sleep(0.03)
    time.sleep(step.get("wait", 0.2))
call(RD, rd, RD + ".Session", "Stop", None)
print("ok")
'''


def partial_timeline(samples: list[dict], audio_started: float, ignore: set[str]) -> dict:
    """First overlay text after audio starts that is not a fixed status label, and every distinct partial."""
    distinct: list[str] = []
    first: float | None = None
    for sample in samples:
        if sample["t"] < audio_started:
            continue
        for text in sample["texts"]:
            if text in ignore or text in distinct:
                continue
            distinct.append(text)
            if first is None:
                first = sample["t"] - audio_started
    return {"first_partial_seconds": None if first is None else round(first, 3), "distinct_partials": distinct}


def parse_x_display(log: str) -> str | None:
    match = re.search(r"Using public X11 display (:\d+)", log)
    return match.group(1) if match else None


def compositor_env(base: dict[str, str], runtime: Path, bus_address: str) -> dict[str, str]:
    env = {key: value for key, value in base.items() if key not in ("WAYLAND_DISPLAY", "DISPLAY")}
    env.update(XDG_RUNTIME_DIR=str(runtime), DBUS_SESSION_BUS_ADDRESS=bus_address,
               PIPEWIRE_RUNTIME_DIR=base.get("XDG_RUNTIME_DIR") or f"/run/user/{os.getuid()}")
    return env


def all_names(tree: list[dict]) -> set[str]:
    names: set[str] = set()
    stack = list(tree)
    while stack:
        node = stack.pop()
        names.add(node.get("name", ""))
        stack.extend(node.get("children", []))
    return names


def find_node(tree: list[dict], name: str, role: str | None = None) -> dict | None:
    stack = list(tree)
    while stack:
        node = stack.pop(0)
        if node.get("name") == name and (role is None or node.get("role") == role):
            return node
        stack.extend(node.get("children", []))
    return None


def center(node: dict) -> list[float]:
    x, y, width, height = node["extents"]
    return [x + width / 2, y + height / 2]


def frame_names(tree: list[dict]) -> list[str]:
    return [child.get("name", "") for app in tree for child in app.get("children", []) if child.get("role") == "frame"]


class HiddenDesktop:
    def __init__(self, workdir: Path, size: str = "1440x900"):
        self.workdir = workdir.resolve()
        self.size = size
        self.runtime: Path | None = None
        self.bus_address = ""
        self.x_display: str | None = None
        self.wayland_display = f"tw-verify-wl-{secrets.token_hex(3)}"
        self.env: dict[str, str] = {}
        self._processes: list[subprocess.Popen] = []

    def __enter__(self) -> "HiddenDesktop":
        try:
            self.start()
        except BaseException:
            self.stop()
            raise
        return self

    def __exit__(self, *exc) -> None:
        self.stop()

    def start(self) -> None:
        for tool in ("mutter", "dbus-daemon", "gst-launch-1.0", A11Y_LAUNCHER, A11Y_REGISTRY):
            if shutil.which(tool) is None and not Path(tool).is_file():
                raise FileNotFoundError(f"{tool} is required for hidden desktop checks")
        self.workdir.mkdir(parents=True, exist_ok=True)
        self.runtime = isolated_app.make_runtime_dir()
        bus = subprocess.Popen(["dbus-daemon", "--session", "--nofork", "--print-address=1"],
                               stdout=subprocess.PIPE, text=True)
        self._processes.append(bus)
        self.bus_address = bus.stdout.readline().strip()
        self.env = compositor_env(dict(os.environ), self.runtime, self.bus_address)
        log = (self.workdir / "desktop.log").open("w", encoding="utf-8")
        # A private bus cannot activate these through systemd, so start them directly.
        self._spawn([A11Y_LAUNCHER, "--launch-immediately", "--a11y=1"], log)
        time.sleep(1)
        self._spawn([A11Y_REGISTRY], log)
        self._spawn(["mutter", "--headless", "--wayland", "--virtual-monitor", self.size,
                     "--wayland-display", self.wayland_display], log)
        deadline = time.monotonic() + 20
        while self.x_display is None and time.monotonic() < deadline:
            self.x_display = parse_x_display((self.workdir / "desktop.log").read_text(encoding="utf-8", errors="replace"))
            time.sleep(0.25)
        if self.x_display is None or next(self.runtime.glob(".mutter-Xwaylandauth*"), None) is None:
            raise RuntimeError(f"mutter did not start Xwayland; see {self.workdir / 'desktop.log'}")

    def _spawn(self, args: list[str], log) -> None:
        self._processes.append(subprocess.Popen(args, env=self.env, stdout=log, stderr=subprocess.STDOUT))

    def stop(self) -> None:
        for process in reversed(self._processes):
            if process.poll() is None:
                process.terminate()
                try:
                    process.wait(timeout=10)
                except subprocess.TimeoutExpired:
                    process.kill()
                    process.wait()
        self._processes.clear()
        runtime, self.runtime = self.runtime, None
        if runtime is not None and runtime.name.startswith("tw-verify-") and runtime.parent == Path("/tmp"):
            shutil.rmtree(runtime, ignore_errors=True)

    def app_env(self) -> dict[str, str]:
        """Environment for `IsolatedApp(display="none", extra_env=...)`."""
        assert self.runtime is not None and self.x_display is not None
        auth = next(self.runtime.glob(".mutter-Xwaylandauth*"))
        return {"DISPLAY": self.x_display, "XAUTHORITY": str(auth), "DBUS_SESSION_BUS_ADDRESS": self.bus_address,
                "WAYLAND_DISPLAY": str(self.runtime / self.wayland_display)}

    def accessibility(self, click: str | None = None) -> dict:
        """The AT-SPI tree, after optionally invoking the first action of the control named `click`."""
        done = subprocess.run(["python3", "-c", _ATSPI_SCRIPT] + ([click] if click else []),
                              env={**os.environ, "DBUS_SESSION_BUS_ADDRESS": self.bus_address},
                              capture_output=True, text=True, timeout=90)
        if not done.stdout.strip():
            raise RuntimeError(f"AT-SPI query failed: {done.stderr[-500:]}")
        return json.loads(done.stdout)

    def input(self, steps: list[dict]) -> None:
        """Click and type inside the hidden compositor only. Steps: {"click": [x, y]}, {"keys": [...]}, {"wait": s}."""
        done = subprocess.run(["python3", "-c", _INPUT_SCRIPT, json.dumps(steps)], env=self.env,
                              capture_output=True, text=True, timeout=60)
        if done.returncode != 0 or "ok" not in done.stdout:
            raise RuntimeError(f"hidden desktop input failed: {(done.stdout + done.stderr)[-500:]}")

    def watch_texts(self, frame: str, seconds: float) -> subprocess.Popen:
        """Start recording every change of the texts shown in `frame`; read JSON lines from stdout."""
        return subprocess.Popen(["python3", "-c", _WATCH_SCRIPT, frame, str(seconds)],
                                env={**os.environ, "DBUS_SESSION_BUS_ADDRESS": self.bus_address},
                                stdout=subprocess.PIPE, stderr=subprocess.DEVNULL, text=True)

    def screenshot(self, path: Path) -> Path:
        path.parent.mkdir(parents=True, exist_ok=True)
        done = subprocess.run(["python3", "-c", _SCREENSHOT_SCRIPT, str(path)], env=self.env,
                              capture_output=True, text=True, timeout=60)
        if done.returncode != 0 or not path.is_file():
            raise RuntimeError(f"screenshot failed: {(done.stdout + done.stderr)[-500:]}")
        return path


def smoke(workdir: Path) -> int:
    """Open the main window in a hidden desktop, click Open Wizard, and record both states."""
    report: dict[str, object] = {}
    with HiddenDesktop(workdir) as desktop:
        with isolated_app.IsolatedApp(workdir / "app", configuration="Release", minimized=False,
                                      display="none", extra_env=desktop.app_env()) as app:
            time.sleep(4)
            before = desktop.accessibility()
            desktop.screenshot(workdir / "main-window.png")
            clicked = desktop.accessibility(click="Open Wizard")["clicked"]
            time.sleep(1.5)
            after = desktop.accessibility()
            desktop.screenshot(workdir / "wizard.png")
            report = {"x_display": desktop.x_display, "api_ready": bool(app.status), "clicked": clicked,
                      "frames_before": frame_names(before["tree"]), "frames_after": frame_names(after["tree"]),
                      "wizard_opened": "Welcome to TypeWhisper" in frame_names(after["tree"])}
            isolated_app.write_json(workdir / "atspi-before.json", before["tree"])
            isolated_app.write_json(workdir / "atspi-after.json", after["tree"])
    isolated_app.write_json(workdir / "hidden-desktop.json", report)
    isolated_app.write_json(workdir / "run.json", isolated_app.run_manifest({"kind": "hidden-desktop-smoke"}))
    print(json.dumps(report, indent=2))
    return 0 if report.get("wizard_opened") and (clicked or {}).get("ok") else 1


def shortcuts(workdir: Path, shortcut: str) -> int:
    """Record a main dictation hotkey through the Shortcuts recorder and check the saved setting."""
    keys = shortcut_keysyms(shortcut)
    report: dict[str, object] = {"shortcut": shortcut}
    with HiddenDesktop(workdir) as desktop:
        with isolated_app.IsolatedApp(workdir / "app", configuration="Release", minimized=False,
                                      display="none", extra_env=desktop.app_env()) as app:
            time.sleep(4)
            report["nav_clicked"] = desktop.accessibility(click="Shortcuts")["clicked"]
            time.sleep(1.0)
            tree = desktop.accessibility()["tree"]
            report["shortcuts_section_open"] = "ShortcutsSection" in all_names(tree)
            box = find_node(tree, "Main dictation hotkey")
            report["hotkey_box_found"] = box is not None and "extents" in box
            if report["shortcuts_section_open"] and report["hotkey_box_found"]:
                desktop.screenshot(workdir / "shortcuts-before.png")
                # The user flow: Record keys, press the combination, then Apply on the main row.
                report["record_clicked"] = desktop.accessibility(click="Record keys")["clicked"]
                time.sleep(0.5)
                desktop.input([{"keys": keys, "wait": 1.0}])
                tree = desktop.accessibility()["tree"]
                after = find_node(tree, "Main dictation hotkey") or {}
                report["box_text_after"] = after.get("text")
                desktop.screenshot(workdir / "shortcuts-captured.png")
                report["apply_clicked"] = desktop.accessibility(click="Apply")["clicked"]
                time.sleep(1.5)
                desktop.screenshot(workdir / "shortcuts-after.png")
            settings_path = app.instance / "data/TypeWhisper/settings.json"
            saved = json.loads(settings_path.read_text(encoding="utf-8"))
            report["saved_toggle_hotkey"] = next((v for k, v in saved.items() if k.lower() == "togglehotkey"), None)
            isolated_app.write_json(workdir / "atspi-after.json", tree)
    report["recorded"] = str(report.get("saved_toggle_hotkey") or "").replace(" ", "").lower() == shortcut.replace(" ", "").lower()
    isolated_app.write_json(workdir / "shortcuts.json", report)
    isolated_app.write_json(workdir / "run.json", isolated_app.run_manifest({"kind": "hidden-desktop-shortcuts", "shortcut": shortcut}))
    print(json.dumps(report, indent=2))
    return 0 if report["recorded"] else 1


def streaming(workdir: Path, audio: Path, plugin: str, engine: str, model: str) -> int:
    """Dictate a public clip in the hidden desktop and time the first live-preview text the overlay shows.

    Delivery goes to the hidden session's clipboard, so the user's clipboard is never written.
    """
    if not audio.is_file():
        raise SystemExit(f"{audio} is missing; prepare the public corpus first")
    settings = {"selectedModelId": f"plugin:{plugin}:{model}", "liveTranscriptionEnabled": True, "cleanupLevel": 0,
                "pluginEnabledState": {"com.typewhisper.filler-words": False}}
    with HiddenDesktop(workdir) as desktop:
        with isolated_app.IsolatedApp(workdir / "app", configuration="Release", settings=settings,
                                      plugin_data=[plugin], virtual_mic=True, display="none",
                                      extra_env=desktop.app_env()) as app:
            app.load_model(engine, model)
            watcher = desktop.watch_texts("TypeWhisper Overlay", seconds=60)
            time.sleep(1.0)
            started = app.api("/v1/dictation/start", {})
            time.sleep(0.3)
            audio_started = time.time()
            player = subprocess.Popen(["paplay", f"--device={app.mic.name}", str(audio)])
            time.sleep(1.5)
            desktop.screenshot(workdir / "overlay-recording.png")
            player.wait(timeout=120)
            time.sleep(2.5)
            stop_at = time.time()
            app.api("/v1/dictation/stop", {})
            path = f"/v1/dictation/transcription?sessionId={started['session_id']}"
            result = app.api(path)
            deadline = time.monotonic() + 15
            while result.get("performance") is None and time.monotonic() < deadline:
                time.sleep(0.25)
                result = app.api(path)
            pasted = subprocess.run(["wl-paste", "--no-newline"], capture_output=True, text=True,
                                    env={**os.environ, "WAYLAND_DISPLAY": desktop.app_env()["WAYLAND_DISPLAY"]}).stdout
            watcher.terminate()
            samples = [json.loads(line) for line in watcher.communicate(timeout=10)[0].splitlines() if line.strip()]
    before = {text for sample in samples if sample["t"] < audio_started for text in sample["texts"]}
    timeline = partial_timeline(samples, audio_started, ignore=before | {str(result.get("text") or "")})
    report = {"audio": audio.name, "first_partial_seconds": timeline["first_partial_seconds"],
              "distinct_partials": timeline["distinct_partials"], "final_text": result.get("text"),
              "stop_to_delivery_ms": (result.get("performance") or {}).get("stop_to_delivery_milliseconds"),
              "delivery_outcome": (result.get("performance") or {}).get("delivery_outcome"),
              "hidden_clipboard_matches": pasted.strip() == str(result.get("text") or "").strip() and bool(pasted.strip()),
              "recorded_seconds": round(stop_at - audio_started, 2)}
    isolated_app.write_json(workdir / "overlay-samples.json", samples)
    isolated_app.write_json(workdir / "streaming.json", report)
    isolated_app.write_json(workdir / "run.json", isolated_app.run_manifest(
        {"kind": "hidden-desktop-streaming", "engine": engine, "model": model, "audio": audio.name}))
    print(json.dumps(report, indent=2))
    return 0 if report["first_partial_seconds"] is not None and report["hidden_clipboard_matches"] else 1


if __name__ == "__main__":
    import argparse
    import datetime as dt

    parser = argparse.ArgumentParser(description="Hidden desktop UI checks.")
    parser.add_argument("--out", type=Path, default=Path(f"artifacts/verification/{dt.date.today()}-hidden-desktop"))
    parser.add_argument("--mode", choices=("smoke", "streaming", "shortcuts"), default="smoke")
    parser.add_argument("--shortcut", default="Ctrl+Shift+F8")
    parser.add_argument("--audio", type=Path, default=Path("/tmp/typewhisper-benchmark/1462-170145-0012.wav"))
    parser.add_argument("--plugin", default="com.typewhisper.sherpa-onnx")
    parser.add_argument("--engine", default="sherpa-onnx")
    parser.add_argument("--model", default="parakeet-tdt-0.6b")
    parsed = parser.parse_args()
    if parsed.mode == "shortcuts":
        sys.exit(shortcuts(parsed.out, parsed.shortcut))
    if parsed.mode == "streaming":
        sys.exit(streaming(parsed.out, parsed.audio.resolve(), parsed.plugin, parsed.engine, parsed.model))
    sys.exit(smoke(parsed.out))
