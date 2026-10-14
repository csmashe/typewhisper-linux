#!/usr/bin/env python3
"""Read-only TypeWhisper Linux and local API health report."""

from __future__ import annotations

import json
import os
import platform
import shutil
import socket
import subprocess
import sys
import xml.etree.ElementTree as ET
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]


def can_connect_tcp(port: int | None) -> bool:
    if port is None:
        return False
    try:
        with socket.create_connection(("127.0.0.1", port), timeout=0.5):
            return True
    except OSError:
        return False


def can_connect_unix(path: str | None) -> bool:
    if not path:
        return False
    try:
        with socket.socket(socket.AF_UNIX, socket.SOCK_STREAM) as client:
            client.settimeout(0.5)
            client.connect(path)
        return True
    except OSError:
        return False


def typewhisper_processes() -> list[dict[str, object]]:
    processes = []
    for entry in Path("/proc").iterdir() if Path("/proc").is_dir() else ():
        if not entry.name.isdigit():
            continue
        try:
            name = (entry / "comm").read_text().strip()
            command = (entry / "cmdline").read_bytes().replace(b"\0", b" ").decode(errors="replace")
        except OSError:
            continue
        if (
            name.casefold() == "typewhisper"
            or "typewhisper.dll" in command.casefold()
            or "TypeWhisper.Linux.dll" in command
        ):
            processes.append({"pid": int(entry.name), "process": name})
    return processes


def typewhisper_command() -> list[str] | None:
    dotnet = os.environ.get("DOTNET") or shutil.which("dotnet")
    if not dotnet:
        for candidate in (
            Path.home() / ".dotnet/dotnet",
            Path("/usr/share/dotnet/dotnet"),
            Path("/tmp/typewhisper-dotnet/dotnet"),
        ):
            if candidate.is_file() and os.access(candidate, os.X_OK):
                dotnet = str(candidate)
                break

    assembly = ROOT / "src/TypeWhisper.Linux/bin/Debug/net10.0/typewhisper.dll"
    if dotnet and assembly.is_file():
        return [dotnet, str(assembly)]

    found = shutil.which("typewhisper")
    if found:
        return [found]
    data_home = Path(os.environ.get("XDG_DATA_HOME", Path.home() / ".local/share"))
    installed = data_home / "typewhisper-app" / "typewhisper"
    if installed.is_file() and os.access(installed, os.X_OK):
        return [str(installed)]
    apphost = ROOT / "src/TypeWhisper.Linux/bin/Debug/net10.0/typewhisper"
    if apphost.is_file() and os.access(apphost, os.X_OK):
        return [str(apphost)]
    return None


def running_status(command: list[str] | None) -> tuple[dict[str, object] | None, str | None]:
    if not command:
        return None, "TypeWhisper command was not found"
    try:
        result = subprocess.run([*command, "status"], capture_output=True, text=True, timeout=5, check=False)
    except (OSError, subprocess.TimeoutExpired) as error:
        return None, type(error).__name__
    for line in reversed(result.stdout.splitlines()):
        try:
            value = json.loads(line)
        except json.JSONDecodeError:
            continue
        if isinstance(value, dict) and "ok" in value:
            return value, None
    return None, f"control status exited with code {result.returncode} and returned no status object"


def source_version() -> str | None:
    try:
        root = ET.parse(ROOT / "Directory.Build.props").getroot()
    except (OSError, ET.ParseError):
        return None
    return next((node.text.strip() for node in root.iter("Version") if node.text and node.text.strip()), None)


def sdk_version() -> str | None:
    dotnet = shutil.which("dotnet")
    if not dotnet:
        for candidate in (Path.home() / ".dotnet/dotnet", Path("/usr/share/dotnet/dotnet"), Path("/tmp/typewhisper-dotnet/dotnet")):
            if candidate.is_file() and os.access(candidate, os.X_OK):
                dotnet = str(candidate)
                break
    if not dotnet:
        return None
    try:
        result = subprocess.run([dotnet, "--version"], capture_output=True, text=True, timeout=5, check=False)
    except (OSError, subprocess.TimeoutExpired):
        return None
    return result.stdout.strip() if result.returncode == 0 else None


def main() -> int:
    data_home = Path(os.environ.get("XDG_DATA_HOME", Path.home() / ".local/share"))
    config_home = Path(os.environ.get("XDG_CONFIG_HOME", Path.home() / ".config"))
    data_root = data_home / "TypeWhisper"
    settings_path = data_root / "settings.json"
    settings: dict[str, object] = {}
    try:
        loaded = json.loads(settings_path.read_text(encoding="utf-8"))
        if isinstance(loaded, dict):
            settings = loaded
    except (OSError, json.JSONDecodeError):
        pass

    def setting(name: str) -> object | None:
        return next((value for key, value in settings.items() if key.lower() == name.lower()), None)

    discovery_path = config_home / "typewhisper/api-discovery.json"
    discovery: dict[str, object] = {}
    try:
        loaded = json.loads(discovery_path.read_text(encoding="utf-8"))
        if isinstance(loaded, dict):
            discovery = loaded
    except (OSError, json.JSONDecodeError):
        pass

    port = discovery.get("port")
    socket_path = discovery.get("socket_path")
    if not isinstance(socket_path, str):
        runtime_home = os.environ.get("XDG_RUNTIME_DIR")
        socket_path = str(Path(runtime_home) / "typewhisper/api.sock") if runtime_home else None
    configured_port = setting("ApiServerPort")
    api_port = port if isinstance(port, int) else configured_port if isinstance(configured_port, int) else None
    audio_path = data_root / "Audio"
    audio_mode = None
    if audio_path.exists():
        try:
            audio_mode = oct(audio_path.stat().st_mode & 0o777)
        except OSError:
            pass
    model_path = data_root / "Models"
    processes = typewhisper_processes()
    command = typewhisper_command()
    status, status_error = running_status(command) if processes else (None, "app process not found")

    try:
        commit = subprocess.run(["git", "-C", str(ROOT), "rev-parse", "--short", "HEAD"], capture_output=True, text=True, check=False).stdout.strip()
    except OSError:
        commit = ""

    report = {
        "source": {
            "root": str(ROOT),
            "commit": commit or None,
            "version": source_version(),
            "sdk_requested": json.loads((ROOT / "global.json").read_text(encoding="utf-8")).get("sdk", {}).get("version"),
            "sdk_installed": sdk_version(),
        },
        "host": {"system": platform.system(), "release": platform.release(), "architecture": platform.machine()},
        "app": {"processes": processes, "control_status": status, "status_error": status_error},
        "api": {
            "discovery_file": str(discovery_path),
            "discovery_present": discovery_path.is_file(),
            "discovery_version": discovery.get("version"),
            "port": api_port,
            "port_source": "discovery" if isinstance(port, int) else "settings" if isinstance(configured_port, int) else None,
            "tcp_port_connectable": can_connect_tcp(api_port),
            "token_present": bool(discovery.get("token")),
            "enabled_in_settings": setting("ApiServerEnabled"),
            "token_configured_in_settings": bool(setting("ApiServerBearerToken")),
            "unix_socket_path": socket_path,
            "unix_socket_exists": bool(socket_path and Path(socket_path).exists()),
            "unix_socket_connectable": can_connect_unix(socket_path),
        },
        "data": {
            "root": str(data_root),
            "settings_exists": settings_path.is_file(),
            "models_exists": model_path.is_dir(),
            "model_directory_count": sum(1 for item in model_path.iterdir() if item.is_dir()) if model_path.is_dir() else 0,
            "logs_exists": (data_root / "Logs").is_dir(),
            "audio_exists": audio_path.is_dir(),
            "audio_directory_mode": audio_mode,
        },
    }
    print(json.dumps(report, indent=2))
    return 0 if isinstance(status, dict) and status.get("ok") is True else 2


if __name__ == "__main__":
    sys.exit(main())
