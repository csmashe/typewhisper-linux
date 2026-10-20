"""Run with uv run --no-project verify_settings.py from the repository root.

Loads the root verification helpers from the known local commit 4f3aa26d.
The TLS fixture accepts only the two actual OpenRouter catalog paths. Its CA
is trusted only by the isolated app process. No DNS or host trust is changed.
"""
import json
import os
from pathlib import Path
import socketserver
import ssl
import subprocess
import sys
import threading
import time

OUT = Path(__file__).resolve().parent
helpers = OUT / "helpers"
helpers.mkdir(exist_ok=True)
for helper in ("isolated_app.py", "hidden_desktop.py"):
    (helpers / helper).write_bytes(subprocess.check_output(["git", "show", f"4f3aa26d:scripts/verification/{helper}"]))
sys.path.insert(0, str(helpers))
import isolated_app as ia
import hidden_desktop as hd

ia.ROOT = Path.cwd()
hd._ATSPI_SCRIPT = hd._ATSPI_SCRIPT.replace("depth > 40", "depth > 90")
hd._ATSPI_SCRIPT = hd._ATSPI_SCRIPT.replace('{"error": type(error).__name__}', '{"error": str(error)}')
hd._ATSPI_SCRIPT = hd._ATSPI_SCRIPT.replace(
    '        text = node.get_text_iface()\n        if text is not None:\n            item["text"] = text.get_text(0, -1)',
    '        try:\n            text = node.get_text_iface()\n            if text is not None: item["text"] = text.get_text(0, -1)\n        except TypeError: pass')
# Navigation buttons expose their text as a child label in this Avalonia build.
hd._ATSPI_SCRIPT = hd._ATSPI_SCRIPT.replace(
    'def find(node, name, depth=0):',
    '''def contains_label(node, name, depth=0):
    if node is None or depth > 12: return False
    if node.get_name() == name: return True
    return any(contains_label(node.get_child_at_index(i), name, depth+1) for i in range(node.get_child_count()))
def find(node, name, depth=0):''').replace(
    'if node.get_name() == name and node.get_action_iface() is not None: return node',
    'if node.get_action_iface() is not None and (node.get_name() == name or (node.get_role_name() == "button" and contains_label(node, name))): return node')
PID = "com.typewhisper.openrouter"
calls = []
mode = "success"
release = threading.Event()
release.set()


class Boundary(socketserver.StreamRequestHandler):
    def handle(self):
        connect = self.rfile.readline().decode().split()
        if len(connect) < 2:
            return
        while self.rfile.readline().strip():
            pass
        if connect[1] != "openrouter.ai:443":
            self.wfile.write(b"HTTP/1.1 502 Bad Gateway\r\nContent-Length: 0\r\n\r\n")
            return
        self.wfile.write(b"HTTP/1.1 200 Connection Established\r\n\r\n")
        self.wfile.flush()
        with context.wrap_socket(self.connection, server_side=True) as conn:
            stream = conn.makefile("rb")
            request = stream.readline().decode().split()
            while stream.readline().strip():
                pass
            path = request[1]
            calls.append({"method": request[0], "host": connect[1], "path": path, "mode": mode})
            ia.write_json(OUT / "provider-requests.json", calls)
            release.wait(25)
            allowed = path in ("/api/v1/models", "/api/v1/models?output_modalities=transcription")
            ok = allowed and mode == "success"
            model = "fixture/audio" if "transcription" in path else "google/gemini-2.5-flash"
            body = json.dumps({"data": [{"id": model, "name": "Fixture model", "architecture": {"modality": "text->text"}}]}).encode() if ok else b"{}"
            conn.sendall(f"HTTP/1.1 {200 if ok else 503} Fixture\r\nContent-Type: application/json\r\nContent-Length: {len(body)}\r\nConnection: close\r\n\r\n".encode() + body)


def snapshot(desktop, name):
    tree = desktop.accessibility()
    ia.write_json(OUT / f"{name}.json", tree)
    return tree


def labels(tree, name):
    return sum((node.get("role") == "label" and node.get("name") == name) + labels(node.get("children", []), name) for node in tree)


def click(desktop, name):
    result = desktop.accessibility(click=name)
    if not (result.get("clicked") or {}).get("ok"):
        node = hd.find_node(result["tree"], name)
        assert node is not None, name
        desktop.input([{"click": hd.center(node)}])
    time.sleep(0.7)
    return result


def scroll_content(desktop):
    original = hd._ATSPI_SCRIPT
    try:
        hd._ATSPI_SCRIPT = original.replace('for i in range(node.get_child_count()):', 'for i in reversed(range(node.get_child_count())):')
        click(desktop, "Page down")
    finally:
        hd._ATSPI_SCRIPT = original


def edit_temperature(desktop, value):
    script = '''import gi, sys
gi.require_version("Atspi", "2.0")
from gi.repository import Atspi
entries=[]
def walk(node, depth=0):
    if node is None or depth>90: return
    if node.get_role_name()=="entry": entries.append(node)
    for i in range(node.get_child_count()): walk(node.get_child_at_index(i),depth+1)
walk(Atspi.get_desktop(0))
assert entries[1].get_editable_text_iface().set_text_contents(sys.argv[1])
print("edited")
'''
    done = subprocess.run([desktop._gi_python(), "-c", script, value], env={**os.environ, "DBUS_SESSION_BUS_ADDRESS": desktop.bus_address}, capture_output=True, text=True, timeout=30)
    assert done.returncode == 0, done.stderr
    time.sleep(0.5)


if __name__ == "__main__":
    tls = OUT / "tls"
    tls.mkdir(exist_ok=True)
    subprocess.run(["openssl", "req", "-x509", "-newkey", "rsa:2048", "-nodes", "-days", "2", "-subj", "/CN=openrouter.ai", "-addext", "subjectAltName=DNS:openrouter.ai", "-keyout", str(tls / "key.pem"), "-out", str(tls / "cert.pem")], check=True, capture_output=True)
    context = ssl.SSLContext(ssl.PROTOCOL_TLS_SERVER)
    context.load_cert_chain(tls / "cert.pem", tls / "key.pem")
    with socketserver.ThreadingTCPServer(("127.0.0.1", 0), Boundary) as server:
        server.daemon_threads = True
        threading.Thread(target=server.serve_forever, daemon=True).start()
        with hd.HiddenDesktop(OUT / "desktop") as desktop:
            proxy = f"http://127.0.0.1:{server.server_address[1]}"
            env = {**desktop.app_env(), "HTTPS_PROXY": proxy, "https_proxy": proxy, "NO_PROXY": "localhost,127.0.0.1", "SSL_CERT_FILE": str(tls / "cert.pem")}
            enabled = {p.parent.name: p.parent.name == PID for p in (ia.ROOT / "src/TypeWhisper.Linux/bin/Release/net10.0/Plugins").glob("*/manifest.json")}
            with ia.IsolatedApp(OUT / "live", configuration="Release", minimized=False, display="none", extra_env=env,
                                settings={"pluginEnabledState": enabled},
                                plugin_settings={PID: {"selectedLlmModel": "google/gemini-2.5-flash", "userSelectedLlmModel": False}}) as app:
                time.sleep(3)
                snapshot(desktop, "initial-tree")
                desktop.screenshot(OUT / "initial.png")
                print("INITIAL", sorted(hd.all_names(desktop.accessibility()["tree"])), flush=True)
                click(desktop, "Plugins")
                snapshot(desktop, "plugins-tree")
                desktop.screenshot(OUT / "plugins.png")
                print("PLUGINS", sorted(hd.all_names(desktop.accessibility()["tree"])), flush=True)
                settings_file = app.instance / f"data/TypeWhisper/PluginData/{PID}/settings.json"
                before = settings_file.read_bytes()
                before_mtime = settings_file.stat().st_mtime_ns
                click(desktop, "⌄")
                assert not calls, calls
                assert settings_file.read_bytes() == before
                assert settings_file.stat().st_mtime_ns == before_mtime
                expanded = snapshot(desktop, "expanded-tree")
                desktop.screenshot(OUT / "expanded.png")
                print("EXPANDED", sorted(hd.all_names(desktop.accessibility()["tree"])), flush=True)
                click(desktop, "Refresh")
                time.sleep(2)
                refreshed = snapshot(desktop, "refreshed-tree")
                assert labels(refreshed["tree"], "API key") == labels(expanded["tree"], "API key"), "Overlapping reloads duplicated fields"
                desktop.screenshot(OUT / "refreshed.png")
                print("REFRESHED", sorted(hd.all_names(desktop.accessibility()["tree"])), flush=True)
                print("PERSISTED", settings_file.read_text(), flush=True)
                cached = json.loads(settings_file.read_text())
                baseline = json.loads(before)
                assert cached["selectedLlmModel"] == baseline["selectedLlmModel"]
                assert cached["userSelectedLlmModel"] == baseline["userSelectedLlmModel"]
                assert cached["fetchedModels"][1]["Id"] == "google/gemini-2.5-flash"
                assert cached["fetchedTranscriptionModels"][0]["Id"] == "fixture/audio"
                assert "Models refreshed." in hd.all_names(desktop.accessibility()["tree"])
                before_unchanged = settings_file.read_bytes()
                mtime = settings_file.stat().st_mtime_ns
                click(desktop, "Refresh")
                time.sleep(1)
                assert settings_file.read_bytes() == before_unchanged
                assert settings_file.stat().st_mtime_ns == mtime
                mode = "failure"
                click(desktop, "Refresh")
                time.sleep(1)
                failure = snapshot(desktop, "failure-tree")
                scroll_content(desktop)
                desktop.screenshot(OUT / "failure.png")
                assert "Model refresh failed. See the error log for details." in hd.all_names(failure["tree"])
                assert settings_file.read_bytes() == before_unchanged
                assert settings_file.stat().st_mtime_ns == mtime
                ia.write_json(OUT / "persisted-catalog.json", cached)
                mode = "success"
                release.clear()
                request_count = len(calls)
                click(desktop, "Refresh")
                deadline = time.monotonic() + 5
                while len(calls) < request_count + 2 and time.monotonic() < deadline:
                    time.sleep(0.1)
                assert len(calls) == request_count + 2
                edit_temperature(desktop, "0.7")
                snapshot(desktop, "inflight-edit-tree")
                release.set()
                time.sleep(2)
                click(desktop, "Save")
                assert json.loads(settings_file.read_text())["llmTemperatureValue"] == 0.7
                snapshot(desktop, "saved-edit-tree")
                ia.write_json(OUT / "settings-results.json", {"expand_no_requests_or_writes": True, "both_catalogs_persisted": True,
                    "selection_and_marker_preserved": True, "unchanged_refresh_no_writes": True, "failure_visible_and_cache_preserved": True,
                    "inflight_edit_survived_refresh_and_save": True})
        server.shutdown()
    ia.write_json(OUT / "run.json", ia.run_manifest({"kind": "pr138-settings", "audio": False}))
