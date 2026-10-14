import importlib.util
from pathlib import Path
import tempfile
import unittest

spec = importlib.util.spec_from_file_location("doctor", Path(__file__).parents[2] / "scripts/verification/doctor.py")
doctor = importlib.util.module_from_spec(spec)
spec.loader.exec_module(doctor)


def sized(path: Path, size: int) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    with path.open("wb") as handle:
        handle.truncate(size)


class PluginModelTests(unittest.TestCase):
    def test_models_are_found_in_each_plugin_layout_not_the_legacy_folder(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            (root / "Models").mkdir()
            sized(root / "PluginData/com.typewhisper.sherpa-onnx/Models/parakeet/encoder.int8.onnx", 3 * 1024 * 1024)
            sized(root / "PluginData/com.typewhisper.sherpa-onnx/Models/parakeet/tokens.txt", 10)
            sized(root / "PluginData/com.typewhisper.parakeet-ctc/model/model.int8.onnx", 2 * 1024 * 1024)
            sized(root / "PluginData/com.typewhisper.openai/settings.json", 200)

            summary = doctor.plugin_model_summary(root)

        self.assertEqual([
            {"plugin": "com.typewhisper.parakeet-ctc", "model_files": 1, "bytes": 2 * 1024 * 1024},
            {"plugin": "com.typewhisper.sherpa-onnx", "model_files": 1, "bytes": 3 * 1024 * 1024},
        ], summary)

    def test_missing_plugin_data_reports_no_models(self):
        with tempfile.TemporaryDirectory() as temp:
            self.assertEqual([], doctor.plugin_model_summary(Path(temp)))


if __name__ == "__main__":
    unittest.main()
