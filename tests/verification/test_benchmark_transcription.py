import importlib.util
import json
from pathlib import Path
import subprocess
import unittest
from unittest.mock import patch

spec = importlib.util.spec_from_file_location("benchmark", Path(__file__).parents[2] / "scripts/verification/benchmark_transcription.py")
benchmark = importlib.util.module_from_spec(spec)
spec.loader.exec_module(benchmark)


class StatusTests(unittest.TestCase):
    def test_actual_api_backend_and_restart_flag_are_read(self):
        response = subprocess.CompletedProcess([], 0, json.dumps({"model": "whistle", "acceleration": {
            "active_backend": "cpu", "requires_restart": True}}), "")
        with patch.object(benchmark, "run_cli", return_value=response):
            self.assertEqual(("cpu", True, "whistle"), benchmark.read_active_backend("cli"))


if __name__ == "__main__":
    unittest.main()
