#!/usr/bin/env python3
"""Start an isolated TypeWhisper instance for verification, benchmarks, and evals.

The instance gets its own HOME, XDG data, config, state, and runtime folders, its own
API port and token, and optionally a PulseAudio null sink as its microphone. It never
reads or writes the user's settings, history, or recordings. Model files are copied
from the user's plugin data with reflinks when requested; the source is read only.

Teardown stops only the process this run started and unloads only the sink it
loaded. The instance folder stays as evidence.
"""

from __future__ import annotations

import datetime as dt
import json
import os
import platform
import secrets
import shutil
import signal
import socket
import subprocess
import tempfile
import threading
import time
import urllib.error
import urllib.request
import xml.etree.ElementTree as ET
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
from typing import Callable

ROOT = Path(__file__).resolve().parents[2]
USER_DATA = Path(os.environ.get("XDG_DATA_HOME", Path.home() / ".local/share")) / "TypeWhisper"

# Settings that keep an isolated instance from touching the user's desktop input,
# media, history, or telemetry. Overrides may change anything else.
SAFE_DEFAULTS: dict[str, object] = {
    "apiServerEnabled": True,
    "hasCompletedOnboarding": True,
    "pluginFirstRunCompleted": True,
    "waylandEvdevHotkeysEnabled": False,
    "toggleHotkey": "",
    "promptPaletteHotkey": "",
    "recentTranscriptionsHotkey": "",
    "copyLastTranscriptionHotkey": "",
    "readLastTranscriptionHotkey": "",
    "transformSelectionHotkey": "",
    "soundFeedbackEnabled": False,
    "spokenFeedbackEnabled": False,
    "pauseMediaDuringRecording": False,
    "audioDuckingEnabled": False,
    "crashReportingEnabled": False,
    "watchFolderAutoStart": False,
    "targetAppCorrectionLearningEnabled": False,
    "autoAddDictionaryCorrections": False,
    "captureLlmProvenance": False,
    "saveToHistoryEnabled": False,
    "autoPaste": False,
    "language": "en",
    "languageHints": ["en"],
    "localModelAcceleration": "cpu",
}
GLOBAL_INPUT_KEYS = {key for key in SAFE_DEFAULTS if key.endswith("Hotkey")} | {"waylandEvdevHotkeysEnabled"}


def find_dotnet(env: dict[str, str] | None = None) -> str:
    env = dict(os.environ if env is None else env)
    if env.get("DOTNET"):
        return env["DOTNET"]
    found = shutil.which("dotnet", path=env.get("PATH"))
    if found:
        return found
    for candidate in (Path("/tmp/typewhisper-dotnet/dotnet"), Path.home() / ".dotnet/dotnet", Path("/usr/share/dotnet/dotnet")):
        if candidate.is_file() and os.access(candidate, os.X_OK):
            return str(candidate)
    raise FileNotFoundError("dotnet was not found. Set DOTNET or install the SDK from global.json.")


def app_assembly(configuration: str) -> Path:
    path = ROOT / f"src/TypeWhisper.Linux/bin/{configuration}/net10.0/typewhisper.dll"
    if not path.is_file():
        recipe = "just build" if configuration == "Debug" else "just build-release"
        raise FileNotFoundError(f"{path} is missing. Run `{recipe}` first.")
    return path


def build_settings(port: int, token: str, overrides: dict[str, object] | None = None,
                   microphone: tuple[int, str] | None = None) -> dict[str, object]:
    overrides = dict(overrides or {})
    for key in GLOBAL_INPUT_KEYS & overrides.keys():
        if overrides[key] not in ("", False, None):
            raise ValueError(f"{key} would grab global input on the user's desktop; isolated instances keep it off")
    settings = {**SAFE_DEFAULTS, **overrides, "apiServerPort": port, "apiServerBearerToken": token}
    if microphone is not None:
        settings["selectedMicrophoneDevice"], settings["selectedMicrophoneDeviceId"] = microphone
    return settings


UNIX_SOCKET_PATH_MAX = 107  # sun_path is 108 bytes including the terminating NUL
APP_SOCKET_SUFFIX = "typewhisper/api.sock"


def check_runtime_dir(runtime: Path) -> None:
    """The app binds sockets under XDG_RUNTIME_DIR; a long path makes the whole API fail to start."""
    longest = len(str(runtime / APP_SOCKET_SUFFIX).encode())
    if longest > UNIX_SOCKET_PATH_MAX:
        raise ValueError(f"socket path under {runtime} is {longest} bytes; Linux allows {UNIX_SOCKET_PATH_MAX}")


def make_runtime_dir() -> Path:
    runtime = Path(tempfile.mkdtemp(prefix="tw-verify-", dir="/tmp"))
    os.chmod(runtime, 0o700)
    check_runtime_dir(runtime)
    return runtime


def isolated_env(base: dict[str, str], instance: Path, runtime: Path, display: str = "inherit",
                 extra: dict[str, str] | None = None) -> dict[str, str]:
    """Move every per-user state location into `instance`, keeping desktop services reachable.

    `runtime` lives outside `instance` because socket paths must stay short.
    """
    check_runtime_dir(runtime)
    env = dict(base)
    user_runtime = base.get("XDG_RUNTIME_DIR") or f"/run/user/{os.getuid()}"
    env.update(
        HOME=str(instance / "home"),
        XDG_DATA_HOME=str(instance / "data"),
        XDG_CONFIG_HOME=str(instance / "config"),
        XDG_STATE_HOME=str(instance / "state"),
        XDG_CACHE_HOME=str(instance / "cache"),
        XDG_RUNTIME_DIR=str(runtime),
        PULSE_SERVER=base.get("PULSE_SERVER") or f"unix:{user_runtime}/pulse/native",
    )
    if display == "inherit":
        wayland = base.get("WAYLAND_DISPLAY")
        if wayland and not wayland.startswith("/"):
            env["WAYLAND_DISPLAY"] = f"{user_runtime}/{wayland}"
    elif display == "none":
        env.pop("WAYLAND_DISPLAY", None)
        env.pop("DISPLAY", None)
    else:
        raise ValueError(f"unknown display mode {display!r}")
    env.update(extra or {})
    return env


def copy_plugin_data(user_data: Path, instance_data: Path, plugin_ids: list[str]) -> list[str]:
    """Reflink-copy whole plugin data folders (models and plugin settings) into the instance."""
    copied = []
    for plugin_id in plugin_ids:
        if not plugin_id or plugin_id in (".", "..") or "/" in plugin_id or plugin_id != Path(plugin_id).name:
            raise ValueError(f"invalid plugin id {plugin_id!r}")
        source = user_data / "PluginData" / plugin_id
        if not source.is_dir():
            raise FileNotFoundError(f"plugin data for {plugin_id} is missing at {source}; download its model in the app first")
        target = instance_data / "PluginData" / plugin_id
        if not target.exists():
            target.parent.mkdir(parents=True, exist_ok=True)
            subprocess.run(["cp", "-a", "--reflink=auto", str(source), str(target)], check=True)
        copied.append(plugin_id)
    return copied


def new_token() -> str:
    """64 hex characters: the only plaintext form the app keeps instead of regenerating."""
    return secrets.token_hex(32)


def read_discovery(config_home: Path) -> tuple[int, str] | None:
    """The port and token the running app published, as the CLI reads them."""
    try:
        value = json.loads((config_home / "typewhisper/api-discovery.json").read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError):
        return None
    port, token = value.get("port"), value.get("token")
    return (port, token) if isinstance(port, int) and isinstance(token, str) and token else None


def clear_stale_discovery(config_home: Path) -> None:
    """A reused instance folder still holds the previous run's port and token."""
    (config_home / "typewhisper/api-discovery.json").unlink(missing_ok=True)


def free_port() -> int:
    with socket.socket() as probe:
        probe.bind(("127.0.0.1", 0))
        return probe.getsockname()[1]


def git(*args: str) -> str:
    try:
        return subprocess.run(["git", "-C", str(ROOT), *args], capture_output=True, text=True, check=False).stdout
    except OSError:
        return ""


def app_version() -> str | None:
    try:
        root = ET.parse(ROOT / "Directory.Build.props").getroot()
    except (OSError, ET.ParseError):
        return None
    return next((node.text.strip() for node in root.iter("Version") if node.text and node.text.strip()), None)


def run_manifest(run: dict[str, object]) -> dict[str, object]:
    """Source and host facts that must match before two runs can be compared."""
    dirty = [line[3:] for line in git("status", "--porcelain", "--untracked-files=no").splitlines() if line.strip()]
    return {
        "created_at": dt.datetime.now(dt.timezone.utc).isoformat(timespec="seconds"),
        "commit": git("rev-parse", "HEAD").strip() or None,
        "branch": git("rev-parse", "--abbrev-ref", "HEAD").strip() or None,
        "dirty_files": dirty,
        "app_version": app_version(),
        "host": {"system": platform.system(), "release": platform.release(), "machine": platform.machine(),
                 "node": platform.node()},
        "run": run,
    }


def write_json(path: Path, value: object) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(value, indent=2) + "\n", encoding="utf-8")


def _pactl(args: list[str]) -> str:
    return subprocess.run(args, capture_output=True, text=True, check=True).stdout


class VirtualMic:
    """A PulseAudio null sink whose monitor the isolated app records from."""

    PCM = "twfixture"

    def __init__(self, name: str = "tw_verify_fixture", runner: Callable[[list[str]], str] = _pactl):
        self.name = name
        self._run = runner
        self.module: str | None = None

    @property
    def source(self) -> str:
        return f"{self.name}.monitor"

    def load(self) -> None:
        if self.module is None:
            self.module = self._run(["pactl", "load-module", "module-null-sink", f"sink_name={self.name}",
                                     f"sink_properties=device.description={self.name}"]).strip()

    def unload(self) -> None:
        module, self.module = self.module, None
        if module:
            self._run(["pactl", "unload-module", module])

    def alsa_config(self) -> str:
        return (f"</usr/share/alsa/alsa.conf>\n"
                f'pcm.{self.PCM} {{ type pulse hint {{ show on description "TypeWhisper verification fixture" }} }}\n')

    def play(self, audio: Path) -> None:
        subprocess.run(["paplay", f"--device={self.name}", str(audio)], check=True)

    def portaudio_device(self, env: dict[str, str]) -> tuple[int, str]:
        """Find the fixture's PortAudio index and the app's stable `name|inputs` ID."""
        probe = (
            "import ctypes,json\n"
            "class D(ctypes.Structure):\n"
            " _fields_=[('v',ctypes.c_int),('name',ctypes.c_char_p),('host',ctypes.c_int),('inputs',ctypes.c_int),"
            "('outputs',ctypes.c_int),('a',ctypes.c_double),('b',ctypes.c_double),('c',ctypes.c_double),"
            "('d',ctypes.c_double),('rate',ctypes.c_double)]\n"
            "pa=ctypes.CDLL('libportaudio.so.2');pa.Pa_GetDeviceInfo.restype=ctypes.POINTER(D)\n"
            "assert pa.Pa_Initialize()==0\n"
            "print(json.dumps([[i,pa.Pa_GetDeviceInfo(i).contents.name.decode(),pa.Pa_GetDeviceInfo(i).contents.inputs]"
            " for i in range(pa.Pa_GetDeviceCount())]))\n"
            "pa.Pa_Terminate()\n"
        )
        result = subprocess.run(["python3", "-c", probe], env=env, capture_output=True, text=True, check=True)
        devices = json.loads(result.stdout.strip().splitlines()[-1])
        for index, name, inputs in devices:
            if name == self.PCM and inputs > 0:
                return index, f"{name}|{inputs}"
        raise RuntimeError(f"PortAudio does not list the {self.PCM} input; check the ALSA pulse plugin")


class FakeLlm:
    """OpenAI-compatible chat endpoint that echoes `dictated_text` after a fixed delay.

    Only `/v1/chat/completions` is served. Any other path returns 404 and is recorded,
    so a malformed base URL fails loudly instead of passing.
    """

    ROUTE = "/v1/chat/completions"
    MODEL = "fixture-cleanup"

    def __init__(self, delay_seconds: float = 0.25):
        self.delay_seconds = delay_seconds
        self.requests: list[dict[str, object]] = []
        self.rejected_paths: list[str] = []
        self._server: ThreadingHTTPServer | None = None

    @property
    def base_url(self) -> str:
        assert self._server is not None
        return f"http://127.0.0.1:{self._server.server_address[1]}"

    def __enter__(self) -> "FakeLlm":
        owner = self

        class Handler(BaseHTTPRequestHandler):
            def log_message(self, *args):
                pass

            def do_GET(self):
                if self.path.split("?")[0] != "/v1/models":
                    owner.rejected_paths.append(self.path)
                    self.send_error(404)
                    return
                body = json.dumps({"object": "list", "data": [{"id": owner.MODEL, "object": "model"}]}).encode()
                self.send_response(200)
                self.send_header("Content-Type", "application/json")
                self.send_header("Content-Length", str(len(body)))
                self.end_headers()
                self.wfile.write(body)

            def do_POST(self):
                length = int(self.headers.get("Content-Length") or 0)
                raw = self.rfile.read(length)
                if self.path.split("?")[0] != owner.ROUTE:
                    owner.rejected_paths.append(self.path)
                    self.send_error(404)
                    return
                prompt = json.loads(raw)["messages"][-1]["content"]
                owner.requests.append({"at": time.time(), "path": self.path, "prompt": prompt})
                try:
                    text = json.loads(prompt[prompt.index("{"):])["dictated_text"]
                except (ValueError, KeyError):
                    self.send_error(400, "prompt has no dictated_text JSON")
                    return
                time.sleep(owner.delay_seconds)
                body = json.dumps({"choices": [{"message": {"role": "assistant", "content": text}}]}).encode()
                self.send_response(200)
                self.send_header("Content-Type", "application/json")
                self.send_header("Content-Length", str(len(body)))
                self.end_headers()
                self.wfile.write(body)

        self._server = ThreadingHTTPServer(("127.0.0.1", 0), Handler)
        threading.Thread(target=self._server.serve_forever, daemon=True).start()
        return self

    def __exit__(self, *exc) -> None:
        if self._server is not None:
            self._server.shutdown()
            self._server.server_close()

    def plugin_overrides(self, model: str = MODEL) -> tuple[dict[str, object], dict[str, object]]:
        """App settings and plugin settings that route cleanup to this endpoint."""
        app = {"defaultLlmProvider": f"plugin:com.typewhisper.openai-compatible:{model}",
               "pluginEnabledState": {"com.typewhisper.openai-compatible": True}}
        plugin = {"baseUrl": self.base_url, "selectedLlmModel": model}
        return app, plugin


class IsolatedApp:
    """Run one isolated TypeWhisper instance under `workdir/instance`."""

    def __init__(self, workdir: Path, *, configuration: str = "Debug", settings: dict[str, object] | None = None,
                 plugin_data: list[str] | None = None, plugin_settings: dict[str, dict[str, object]] | None = None,
                 virtual_mic: bool = False, display: str = "inherit", user_data: Path = USER_DATA,
                 ready_timeout: float = 45.0, minimized: bool = True, extra_env: dict[str, str] | None = None):
        self.workdir = workdir.resolve()
        self.instance = self.workdir / "instance"
        self.configuration = configuration
        self.overrides = dict(settings or {})
        self.plugin_data = list(plugin_data or [])
        self.plugin_settings = dict(plugin_settings or {})
        self.mic = VirtualMic() if virtual_mic else None
        self.display = display
        self.user_data = user_data
        self.ready_timeout = ready_timeout
        self.minimized = minimized
        self.extra_env = dict(extra_env or {})
        self.port = free_port()
        self.token = new_token()
        self.token_rotated_by_app = False
        self.process: subprocess.Popen | None = None
        self.runtime: Path | None = None
        self.env: dict[str, str] = {}
        self.status: dict[str, object] | None = None

    # Lifecycle -----------------------------------------------------------------------------
    def __enter__(self) -> "IsolatedApp":
        try:
            self.start()
        except BaseException:
            self.stop()
            raise
        return self

    def __exit__(self, *exc) -> None:
        self.stop()

    def start(self) -> None:
        dotnet = find_dotnet()
        assembly = app_assembly(self.configuration)
        for name in ("home", "data", "config", "state", "cache"):
            (self.instance / name).mkdir(parents=True, exist_ok=True)
        self.runtime = make_runtime_dir()
        data = self.instance / "data/TypeWhisper"
        data.mkdir(parents=True, exist_ok=True)

        extra = {"DOTNET_ROOT": str(Path(dotnet).resolve().parent), **self.extra_env}
        microphone = None
        if self.mic is not None:
            alsa = self.instance / "alsa.conf"
            alsa.write_text(self.mic.alsa_config(), encoding="utf-8")
            extra.update(ALSA_CONFIG_PATH=str(alsa), PULSE_SOURCE=self.mic.source)
        self.env = isolated_env(dict(os.environ), self.instance, self.runtime, self.display, extra)
        if self.mic is not None:
            self.mic.load()
            microphone = self.mic.portaudio_device(self.env)

        copy_plugin_data(self.user_data, self.instance / "data/TypeWhisper", self.plugin_data)
        for plugin_id, values in self.plugin_settings.items():
            copy_target = data / "PluginData" / plugin_id
            copy_target.mkdir(parents=True, exist_ok=True)
            write_json(copy_target / "settings.json", values)

        clear_stale_discovery(self.instance / "config")
        settings = build_settings(self.port, self.token, self.overrides, microphone)
        enabled = dict(settings.get("pluginEnabledState") or {})
        for plugin_id in self.plugin_data:
            enabled.setdefault(plugin_id, True)
        settings["pluginEnabledState"] = enabled
        write_json(data / "settings.json", settings)

        log = (self.workdir / "app.log").open("w", encoding="utf-8")
        arguments = [dotnet, str(assembly)] + (["--minimized"] if self.minimized else [])
        self.process = subprocess.Popen(arguments, env=self.env, stdout=log,
                                        stderr=subprocess.STDOUT, stdin=subprocess.DEVNULL, start_new_session=True,
                                        cwd=str(self.instance / "home"))
        self.status = self._wait_ready()

    def _wait_ready(self) -> dict[str, object]:
        deadline = time.monotonic() + self.ready_timeout
        last_error: Exception | None = None
        while time.monotonic() < deadline:
            if self.process is not None and self.process.poll() is not None:
                raise RuntimeError(f"isolated app exited with code {self.process.returncode}; see {self.workdir / 'app.log'}")
            discovered = read_discovery(self.instance / "config")
            if discovered is None:
                time.sleep(0.5)
                continue
            if discovered[0] != self.port:
                raise RuntimeError(f"app published port {discovered[0]}, not the seeded {self.port}")
            if discovered[1] != self.token:
                # SecretProtectionMigrationService replaces seeded plaintext tokens at startup.
                # Use the published token, as the CLI does, and record that it changed.
                self.token = discovered[1]
                self.token_rotated_by_app = True
            try:
                return self.api("/v1/status", timeout=2)
            except (OSError, ValueError) as error:
                last_error = error
                time.sleep(0.5)
        raise TimeoutError(f"isolated app API was not ready in {self.ready_timeout}s: {last_error}")

    def stop(self) -> None:
        process, self.process = self.process, None
        if process is not None and process.poll() is None:
            try:
                os.killpg(process.pid, signal.SIGTERM)
            except ProcessLookupError:
                pass
            try:
                process.wait(timeout=15)
            except subprocess.TimeoutExpired:
                os.killpg(process.pid, signal.SIGKILL)
                process.wait()
        if self.mic is not None:
            self.mic.unload()
        runtime, self.runtime = self.runtime, None
        if runtime is not None and runtime.name.startswith("tw-verify-") and runtime.parent == Path("/tmp"):
            shutil.rmtree(runtime, ignore_errors=True)

    # API -----------------------------------------------------------------------------------
    def api(self, path: str, data: object | None = None, timeout: float = 60) -> dict:
        body = json.dumps(data).encode() if data is not None else None
        request = urllib.request.Request(f"http://127.0.0.1:{self.port}{path}", data=body, headers={
            "Authorization": f"Bearer {self.token}", "Content-Type": "application/json"})
        with urllib.request.urlopen(request, timeout=timeout) as response:
            return json.load(response)

    def load_model(self, engine: str, model: str, timeout: float = 90) -> dict:
        """Wait for the plugin to list `model`, then load it. Retries the app's 409 while plugins start."""
        deadline = time.monotonic() + timeout
        while model not in json.dumps(self.api("/v1/models")):
            if time.monotonic() > deadline:
                raise TimeoutError(f"model {model} was not listed by the isolated app")
            time.sleep(0.5)
        while True:
            try:
                return self.api("/v1/models/load", {"engine": engine, "model": model}, timeout=timeout)
            except urllib.error.HTTPError as error:
                reason = error.read().decode(errors="replace")
                if error.code != 409 or time.monotonic() > deadline:
                    raise RuntimeError(f"model load failed ({error.code}): {reason}") from error
                time.sleep(0.5)

    def capture_sources(self) -> list[str]:
        """PulseAudio sources that this app process is recording from."""
        sources = {s["index"]: s["name"] for s in json.loads(_pactl(["pactl", "--format=json", "list", "sources"]))}
        outputs = json.loads(_pactl(["pactl", "--format=json", "list", "source-outputs"]))
        pid = str(self.process.pid) if self.process else ""
        return [sources.get(o["source"], str(o["source"])) for o in outputs
                if o.get("properties", {}).get("application.process.id") == pid]


if __name__ == "__main__":
    import argparse

    parser = argparse.ArgumentParser(description="Start an isolated TypeWhisper instance and print its API port.")
    parser.add_argument("--workdir", type=Path, required=True)
    parser.add_argument("--configuration", default="Debug", choices=("Debug", "Release"))
    parser.add_argument("--plugin-data", action="append", default=[], help="plugin ID whose models to copy")
    parser.add_argument("--virtual-mic", action="store_true")
    args = parser.parse_args()
    with IsolatedApp(args.workdir, configuration=args.configuration, plugin_data=args.plugin_data,
                     virtual_mic=args.virtual_mic) as app:
        print(json.dumps({"port": app.port, "pid": app.process.pid if app.process else None,
                          "workdir": str(args.workdir), "status": app.status}), flush=True)
        print("Press Ctrl+C to stop the isolated instance.", flush=True)
        try:
            while app.process and app.process.poll() is None:
                time.sleep(1)
        except KeyboardInterrupt:
            pass
