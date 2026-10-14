import importlib.util
from pathlib import Path
import sys
import unittest

sys.path.insert(0, str(Path(__file__).parents[2] / "scripts/verification"))
spec = importlib.util.spec_from_file_location("hidden_desktop", Path(__file__).parents[2] / "scripts/verification/hidden_desktop.py")
hidden = importlib.util.module_from_spec(spec)
spec.loader.exec_module(hidden)


class MutterLogTests(unittest.TestCase):
    def test_reads_the_public_xwayland_display(self):
        log = ("libmutter-Message: Running Mutter (using mutter 51.0) as a Wayland display server\n"
               "libmutter-Message: Using public X11 display :2, (using unix:/tmp/.X11-unix/X3 for managed services)\n")
        self.assertEqual(":2", hidden.parse_x_display(log))

    def test_missing_display_is_none(self):
        self.assertIsNone(hidden.parse_x_display("Running Mutter as a Wayland display server\n"))


class EnvironmentTests(unittest.TestCase):
    def test_compositor_never_sees_the_users_display_or_bus(self):
        base = {"WAYLAND_DISPLAY": "wayland-0", "DISPLAY": ":0", "DBUS_SESSION_BUS_ADDRESS": "unix:path=/run/user/1000/bus",
                "XDG_RUNTIME_DIR": "/run/user/1000", "HOME": "/home/u"}
        env = hidden.compositor_env(base, Path("/tmp/tw-verify-x"), "unix:path=/tmp/private")
        self.assertNotIn("WAYLAND_DISPLAY", env)
        self.assertNotIn("DISPLAY", env)
        self.assertEqual("unix:path=/tmp/private", env["DBUS_SESSION_BUS_ADDRESS"])
        self.assertEqual("/tmp/tw-verify-x", env["XDG_RUNTIME_DIR"])
        # mutter's screencast streams go through the user's PipeWire daemon.
        self.assertEqual("/run/user/1000", env["PIPEWIRE_RUNTIME_DIR"])


class TreeTests(unittest.TestCase):
    TREE = [{"role": "application", "name": "Avalonia Application", "children": [
        {"role": "frame", "name": "TypeWhisper", "children": [{"role": "button", "name": "Open Wizard"}]},
        {"role": "frame", "name": "Welcome to TypeWhisper"}]}]

    def test_named_controls_are_found_and_clicked_at_their_center(self):
        tree = [{"role": "frame", "name": "TypeWhisper", "children": [
            {"role": "text", "name": "Main dictation hotkey", "extents": [100, 200, 40, 20]}]}]
        node = hidden.find_node(tree, "Main dictation hotkey", role="text")
        self.assertEqual([120.0, 210.0], hidden.center(node))
        self.assertIsNone(hidden.find_node(tree, "Main dictation hotkey", role="button"))

    def test_frames_and_names_are_collected(self):
        self.assertEqual(["TypeWhisper", "Welcome to TypeWhisper"], hidden.frame_names(self.TREE))
        self.assertIn("Open Wizard", hidden.all_names(self.TREE))


class KeysymTests(unittest.TestCase):
    def test_shortcut_text_becomes_modifier_then_key_keysyms(self):
        # X keysyms: Control_L 0xffe3, Shift_L 0xffe1, F8 0xffc5, lowercase letters are their ASCII code.
        self.assertEqual([0xffe3, 0xffe1, 0xffc5], hidden.shortcut_keysyms("Ctrl+Shift+F8"))
        self.assertEqual([0xffeb, ord("k")], hidden.shortcut_keysyms("Meta+K"))
        self.assertEqual([0xff1b], hidden.shortcut_keysyms("Escape"))

    def test_unknown_key_names_are_rejected(self):
        with self.assertRaises(ValueError):
            hidden.shortcut_keysyms("Ctrl+Hyperdrive")


class PartialTimelineTests(unittest.TestCase):
    def test_first_partial_is_the_first_overlay_text_after_audio_starts(self):
        samples = [{"t": 10.0, "texts": []}, {"t": 10.4, "texts": ["Listening"]},
                   {"t": 11.2, "texts": ["Listening", "He bent"]}, {"t": 12.0, "texts": ["He bent his face"]}]
        timeline = hidden.partial_timeline(samples, audio_started=10.2, ignore={"Listening"})
        self.assertAlmostEqual(1.0, timeline["first_partial_seconds"])
        self.assertEqual(["He bent", "He bent his face"], timeline["distinct_partials"])

    def test_no_partial_text_is_reported_as_none(self):
        timeline = hidden.partial_timeline([{"t": 1.0, "texts": ["Listening"]}], audio_started=0.5, ignore={"Listening"})
        self.assertIsNone(timeline["first_partial_seconds"])
        self.assertEqual([], timeline["distinct_partials"])


if __name__ == "__main__":
    unittest.main()
