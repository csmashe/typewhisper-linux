import importlib.util
from pathlib import Path
import unittest

spec = importlib.util.spec_from_file_location("comparison", Path(__file__).parents[2] / "scripts/verification/compare_benchmarks.py")
comparison = importlib.util.module_from_spec(spec)
spec.loader.exec_module(comparison)


def report(seconds, errors=0):
    return {"engine": "example", "model": "model", "observed_backend": "cpu", "rows": [
        {"id": str(i), "reference": "one two", "duration_seconds": 2.0,
         "complete_response_seconds": value, "word_errors": errors, "reference_words": 2}
        for i, value in enumerate(seconds)], "sample_count": len(seconds)}


class ComparisonTests(unittest.TestCase):
    def test_speed_and_quality_are_separate_and_first_request_is_excluded(self):
        result = comparison.compare(report([10, 2, 4]), report([20, 1, 2], errors=1))
        self.assertEqual(2, result["warm_median_speedup"])
        self.assertEqual(0.5, result["wer_delta"])
        self.assertEqual(0.5, result["first_request_speedup"])
        self.assertEqual(4, result["baseline"]["warm_p95_seconds"])

    def test_changed_corpus_or_partial_run_is_rejected(self):
        baseline = report([1, 2])
        changed = report([1, 2])
        changed["rows"][1]["reference"] = "different"
        with self.assertRaisesRegex(ValueError, "corpus"):
            comparison.compare(baseline, changed)
        partial = report([1, 2])
        partial["error"] = "provider failed"
        with self.assertRaisesRegex(ValueError, "incomplete"):
            comparison.compare(baseline, partial)

    def test_one_request_cannot_claim_a_warm_speedup(self):
        result = comparison.compare(report([1]), report([0.5]))
        self.assertIsNone(result["warm_median_speedup"])


if __name__ == "__main__":
    unittest.main()
