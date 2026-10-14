import importlib.util
from pathlib import Path
import sys
import unittest

sys.path.insert(0, str(Path(__file__).parents[2] / "scripts/verification"))
spec = importlib.util.spec_from_file_location("live_smoke", Path(__file__).parents[2] / "scripts/verification/live_smoke.py")
smoke = importlib.util.module_from_spec(spec)
spec.loader.exec_module(smoke)


def session(outcome="CopiedToClipboard", matches=True, before_stop=1, stop_ms=800.0, cleanup_ms=1.2):
    return {"result": {"state": "ready", "text": "Hi.", "performance": {
                "delivery_outcome": outcome, "stop_to_delivery_milliseconds": stop_ms,
                "transcription_milliseconds": 130.0, "processing_milliseconds": {"Cleanup": cleanup_ms}}},
            "clipboard_matches": matches, "llm_requests_before_stop": before_stop, "llm_rejected_paths": []}


class SessionCheckTests(unittest.TestCase):
    def test_ready_text_without_delivery_fails(self):
        checks = smoke.session_checks(session(outcome="Failed"), live_cleanup=False)
        self.assertTrue(checks["text_ready"])
        self.assertFalse(checks["delivered_to_clipboard"])

    def test_clipboard_mismatch_fails_even_when_the_app_reports_success(self):
        self.assertFalse(smoke.session_checks(session(matches=False), live_cleanup=False)["clipboard_read_back_matches"])

    def test_live_cleanup_must_send_a_draft_before_stop(self):
        self.assertFalse(smoke.session_checks(session(before_stop=0), live_cleanup=True)["live_cleanup_ran_before_stop"])
        self.assertNotIn("live_cleanup_ran_before_stop", smoke.session_checks(session(before_stop=0), live_cleanup=False))


class SessionLockTests(unittest.TestCase):
    # The app rejects dictation while the user's session is locked ("Start rejected: session locked").
    def test_locked_hint_is_read_from_loginctl_output(self):
        self.assertTrue(smoke.parse_locked_hint("LockedHint=yes\n"))
        self.assertFalse(smoke.parse_locked_hint("LockedHint=no\n"))
        self.assertIsNone(smoke.parse_locked_hint(""))


class TimingTests(unittest.TestCase):
    def test_median_and_worst_per_stage_and_step(self):
        timings = smoke.summarize_timings([session(stop_ms=700), session(stop_ms=900), session(stop_ms=2000, cleanup_ms=300)])
        self.assertEqual({"median": 900.0, "max": 2000.0, "n": 3}, timings["stop_to_delivery_milliseconds"])
        self.assertEqual(300.0, timings["step.Cleanup"]["max"])
        self.assertNotIn("delivery_milliseconds", timings)


if __name__ == "__main__":
    unittest.main()
