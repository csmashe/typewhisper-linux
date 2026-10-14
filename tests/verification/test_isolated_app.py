import importlib.util
import json
from pathlib import Path
import sys
import tempfile
import unittest
import urllib.error
import urllib.request

spec = importlib.util.spec_from_file_location("isolated_app", Path(__file__).parents[2] / "scripts/verification/isolated_app.py")
isolated = importlib.util.module_from_spec(spec)
sys.modules["isolated_app"] = isolated
spec.loader.exec_module(isolated)


class SettingsTests(unittest.TestCase):
    def test_isolated_settings_never_grab_global_input_or_reuse_user_ports(self):
        settings = isolated.build_settings(port=19991, token="t", overrides={"cleanupLevel": 2})
        self.assertTrue(settings["apiServerEnabled"])
        self.assertEqual(19991, settings["apiServerPort"])
        self.assertEqual("t", settings["apiServerBearerToken"])
        self.assertFalse(settings["waylandEvdevHotkeysEnabled"])
        self.assertEqual("", settings["toggleHotkey"])
        self.assertTrue(settings["hasCompletedOnboarding"])
        self.assertEqual(2, settings["cleanupLevel"])

    def test_overrides_cannot_enable_global_hotkeys(self):
        with self.assertRaisesRegex(ValueError, "global input"):
            isolated.build_settings(port=1, token="t", overrides={"waylandEvdevHotkeysEnabled": True})
        with self.assertRaisesRegex(ValueError, "global input"):
            isolated.build_settings(port=1, token="t", overrides={"toggleHotkey": "Ctrl+Space"})

    def test_microphone_selects_fixture_by_index_and_stable_id(self):
        settings = isolated.build_settings(port=1, token="t", microphone=(7, "twfixture|32"))
        self.assertEqual(7, settings["selectedMicrophoneDevice"])
        self.assertEqual("twfixture|32", settings["selectedMicrophoneDeviceId"])


class TokenTests(unittest.TestCase):
    def test_generated_token_survives_the_apps_plaintext_migration(self):
        # The app keeps a plaintext token only when it is 64 hex characters
        # (HttpApiService.LegacyPlaintextTokenRegex); anything else is replaced.
        token = isolated.new_token()
        self.assertRegex(token, r"^[0-9a-f]{64}$")
        self.assertNotEqual(token, isolated.new_token())

    def test_discovery_file_is_the_source_of_the_effective_port_and_token(self):
        with tempfile.TemporaryDirectory() as temp:
            config = Path(temp)
            self.assertIsNone(isolated.read_discovery(config))
            path = config / "typewhisper/api-discovery.json"
            path.parent.mkdir(parents=True)
            path.write_text(json.dumps({"version": 2, "port": 4321, "token": "abc", "socket_path": "/tmp/x"}))
            self.assertEqual((4321, "abc"), isolated.read_discovery(config))
            path.write_text("{not json")
            self.assertIsNone(isolated.read_discovery(config))

    def test_a_reused_instance_folder_does_not_report_the_previous_runs_port(self):
        with tempfile.TemporaryDirectory() as temp:
            config = Path(temp)
            path = config / "typewhisper/api-discovery.json"
            path.parent.mkdir(parents=True)
            path.write_text(json.dumps({"port": 1111, "token": "old"}))
            isolated.clear_stale_discovery(config)
            self.assertIsNone(isolated.read_discovery(config))
            isolated.clear_stale_discovery(config)  # missing file is fine


class EnvironmentTests(unittest.TestCase):
    def test_every_user_state_location_moves_into_the_instance(self):
        base = {"HOME": "/home/u", "XDG_RUNTIME_DIR": "/run/user/1000", "WAYLAND_DISPLAY": "wayland-0",
                "DISPLAY": ":0", "DBUS_SESSION_BUS_ADDRESS": "unix:path=/run/user/1000/bus",
                "XDG_DATA_HOME": "/home/u/.local/share"}
        env = isolated.isolated_env(base, Path("/w/instance"), Path("/tmp/tw-x"), display="inherit")
        self.assertEqual("/w/instance/home", env["HOME"])
        self.assertEqual("/w/instance/data", env["XDG_DATA_HOME"])
        self.assertEqual("/w/instance/config", env["XDG_CONFIG_HOME"])
        self.assertEqual("/w/instance/state", env["XDG_STATE_HOME"])
        self.assertEqual("/tmp/tw-x", env["XDG_RUNTIME_DIR"])
        # Shared desktop services stay reachable through absolute paths.
        self.assertEqual("/run/user/1000/wayland-0", env["WAYLAND_DISPLAY"])
        self.assertEqual("unix:/run/user/1000/pulse/native", env["PULSE_SERVER"])
        self.assertEqual("unix:path=/run/user/1000/bus", env["DBUS_SESSION_BUS_ADDRESS"])

    def test_runtime_folder_must_fit_unix_socket_paths(self):
        # The app binds XDG_RUNTIME_DIR/typewhisper/api.sock. Linux limits socket paths to
        # 108 bytes, and a failed bind stops the TCP API as well.
        with self.assertRaisesRegex(ValueError, "socket path"):
            isolated.check_runtime_dir(Path("/home/u/" + "x" * 100 + "/runtime"))
        isolated.check_runtime_dir(Path("/tmp/tw-abc123"))

    def test_launcher_runtime_folder_is_short_and_private(self):
        runtime = isolated.make_runtime_dir()
        try:
            self.assertLess(len(str(runtime / "typewhisper/api.sock").encode()), 108)
            self.assertEqual(0o700, runtime.stat().st_mode & 0o777)
        finally:
            runtime.rmdir()

    def test_no_display_removes_desktop_connections(self):
        env = isolated.isolated_env({"XDG_RUNTIME_DIR": "/run/user/1000", "WAYLAND_DISPLAY": "wayland-0", "DISPLAY": ":0"},
                                    Path("/w/i"), Path("/tmp/tw-x"), display="none")
        self.assertNotIn("WAYLAND_DISPLAY", env)
        self.assertNotIn("DISPLAY", env)


class PluginCopyTests(unittest.TestCase):
    def test_plugin_ids_cannot_escape_the_plugin_data_folder(self):
        with tempfile.TemporaryDirectory() as temp:
            for bad in ("../secret", "a/b", "", "."):
                with self.assertRaises(ValueError):
                    isolated.copy_plugin_data(Path(temp) / "src", Path(temp) / "dst", [bad])

    def test_missing_plugin_data_is_an_error_not_a_silent_skip(self):
        with tempfile.TemporaryDirectory() as temp:
            (Path(temp) / "src/PluginData").mkdir(parents=True)
            with self.assertRaisesRegex(FileNotFoundError, "com.typewhisper.sherpa-onnx"):
                isolated.copy_plugin_data(Path(temp) / "src", Path(temp) / "dst", ["com.typewhisper.sherpa-onnx"])


class VirtualMicTests(unittest.TestCase):
    def test_teardown_unloads_only_the_module_this_run_loaded(self):
        calls = []

        def runner(args):
            calls.append(args)
            return "4242\n" if args[1] == "load-module" else ""

        mic = isolated.VirtualMic(name="tw_test", runner=runner)
        mic.load()
        mic.unload()
        mic.unload()
        self.assertEqual(["pactl", "unload-module", "4242"], calls[-1])
        self.assertEqual(1, sum(1 for call in calls if call[1] == "unload-module"))


class FakeLlmTests(unittest.TestCase):
    def post(self, url, payload):
        request = urllib.request.Request(url, data=json.dumps(payload).encode(), headers={"Content-Type": "application/json"})
        with urllib.request.urlopen(request, timeout=5) as response:
            return json.load(response)

    def test_echoes_dictated_text_on_the_real_route(self):
        with isolated.FakeLlm(delay_seconds=0) as llm:
            prompt = 'Clean this.\n\n{"dictated_text":"um send it tuesday"}'
            body = self.post(llm.base_url + "/v1/chat/completions", {"messages": [{"role": "user", "content": prompt}]})
        self.assertEqual("um send it tuesday", body["choices"][0]["message"]["content"])
        self.assertEqual(["/v1/chat/completions"], [r["path"] for r in llm.requests])

    def test_lists_its_model_like_an_openai_compatible_server(self):
        with isolated.FakeLlm(delay_seconds=0) as llm:
            with urllib.request.urlopen(llm.base_url + "/v1/models", timeout=5) as response:
                body = json.load(response)
        self.assertEqual(["fixture-cleanup"], [model["id"] for model in body["data"]])

    def test_doubled_version_prefix_is_rejected_and_recorded(self):
        with isolated.FakeLlm(delay_seconds=0) as llm:
            with self.assertRaises(urllib.error.HTTPError) as caught:
                self.post(llm.base_url + "/v1/v1/chat/completions", {"messages": [{"role": "user", "content": "{}"}]})
        self.assertEqual(404, caught.exception.code)
        self.assertEqual(["/v1/v1/chat/completions"], llm.rejected_paths)


class ManifestTests(unittest.TestCase):
    def test_manifest_records_source_state_and_run_fields(self):
        manifest = isolated.run_manifest({"model": "parakeet-tdt-0.6b", "backend": "cpu"})
        for key in ("commit", "dirty_files", "app_version", "host", "created_at"):
            self.assertIn(key, manifest)
        self.assertEqual("parakeet-tdt-0.6b", manifest["run"]["model"])
        self.assertIsInstance(manifest["dirty_files"], list)


if __name__ == "__main__":
    unittest.main()
