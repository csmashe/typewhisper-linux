import importlib.util
from pathlib import Path
import unittest

spec = importlib.util.spec_from_file_location("evals", Path(__file__).parents[2] / "scripts/verification/dictation_evals.py")
evals = importlib.util.module_from_spec(spec)
spec.loader.exec_module(evals)


def case(identifier="one"):
    return {"id": identifier, "category": "meaning", "input": "um do not send 47 files",
            "expected_behavior": "Preserve the negation and count.", "example_output": "Do not send 47 files.",
            "language": "en", "profile": "plain", "dictionary": [],
            "required_terms": ["47"], "forbidden_terms": []}


class EvalTests(unittest.TestCase):
    def test_missing_results_are_pending_and_never_count_as_passes(self):
        rows = evals.prepare([case(), case("two")], [{"id": "one", "status": "completed", "text": "Do not send 47 files."}])
        self.assertEqual("pending", rows[1]["status"])
        summary = evals.summarize(rows, [], [])
        self.assertEqual(1, summary["completed"])
        self.assertEqual(0, summary["human_reviewed"])
        self.assertIsNone(summary["human_pass_rate"])

    def test_report_escapes_candidate_html_and_checks_full_terms(self):
        rows = evals.prepare([case()], [{"id": "one", "status": "completed", "text": '<script>alert(1)</script> 470 files'}])
        self.assertIn("47", rows[0]["missing_terms"])
        report = evals.render_review(rows)
        self.assertIn("&lt;script&gt;", report)
        self.assertNotIn("<script>", report)

    def test_duplicate_and_unknown_results_are_rejected(self):
        with self.assertRaisesRegex(ValueError, "duplicate"):
            evals.prepare([case()], [{"id": "one"}, {"id": "one"}])
        with self.assertRaisesRegex(ValueError, "unknown"):
            evals.prepare([case()], [{"id": "other", "status": "completed", "text": "x"}])

    def test_judge_cannot_pass_a_critical_meaning_error(self):
        response = {"id": "one", "verdict": "pass", "critique": "Changed the number",
                    "critical_errors": ["47 became 74"]}
        with self.assertRaisesRegex(ValueError, "critical"):
            evals.validate_judgment(response)

    def test_judge_cannot_pass_a_failed_criterion(self):
        with self.assertRaisesRegex(ValueError, "criterion"):
            evals.validate_judgment({"id":"one", "verdict":"pass", "critique":"Lost negation",
                "critical_errors":[], "criteria":{"meaning":"fail"}})

    def test_unreviewed_manual_templates_are_not_reviews(self):
        rows = evals.prepare([case()], [{"id":"one", "status":"completed", "text":"Do not send 47 files."}])
        summary = evals.summarize(rows, [{"id":"one", "verdict":"pending", "critique":""}], [])
        self.assertEqual(0, summary["human_reviewed"])
        self.assertIsNone(summary["human_pass_rate"])

    def test_failures_are_grouped_by_case_category_with_explicit_coverage(self):
        repair = {**case("two"), "category":"repairs"}
        rows = evals.prepare([case(), repair, {**case("three"), "category":"fillers"}], [
            {"id":"one", "status":"completed", "text":"Send 47 files."},
            {"id":"two", "status":"completed", "text":"Do not send 47 files."}])
        summary = evals.summarize(rows, [{"id":"one", "verdict":"fail", "critique":"Lost not"},
            {"id":"two", "verdict":"pass", "critique":"Meaning preserved"}], [])
        self.assertEqual(1, summary["human_by_category"]["meaning"]["failed"])
        self.assertEqual(1, summary["human_by_category"]["repairs"]["pass_rate"])
        self.assertIsNone(summary["human_by_category"]["fillers"]["pass_rate"])

    def test_judge_disagreement_is_reported_without_treating_judge_as_truth(self):
        rows = evals.prepare([case(), case("two")], [{"id": "one", "status": "completed", "text": "Send 74 files."}])
        human = [{"id": "one", "verdict": "fail", "critique": "Lost negation and changed count"}]
        judge = [{"id": "one", "verdict": "pass", "critique": "Looks fluent", "critical_errors": []}]
        summary = evals.summarize(rows, human, judge)
        self.assertEqual(["one"], summary["judge_false_pass_ids"])
        self.assertEqual(0, summary["judge_human_agreement"])
        self.assertEqual(0, summary["human_pass_rate"])

    def test_critical_failures_and_manual_edits_require_explicit_human_labels(self):
        rows = evals.prepare([case(), case("two"), case("three")], [
            {"id": identifier, "status":"completed", "text":"Send 47 files."}
            for identifier in ("one", "two", "three")])
        summary = evals.summarize(rows, [
            {"id":"one", "verdict":"fail", "critique":"Lost not", "critical_errors":["Lost negation"], "needs_cleanup_edit":True},
            {"id":"two", "verdict":"pass", "critique":"Ready for chat", "critical_errors":[], "needs_cleanup_edit":False},
            {"id":"three", "verdict":"uncertain", "critique":"Need audio to attribute error"}], [])
        self.assertEqual(["one"], summary["human_critical_failure_ids"])
        self.assertEqual(2, summary["human_critical_assessed"])
        self.assertEqual(2, summary["human_cleanup_edit_assessed"])
        self.assertEqual(1, summary["human_cleanup_edits_needed"])
        self.assertEqual(0.5, summary["human_cleanup_edit_rate"])

    def test_absent_quality_annotations_remain_unmeasured(self):
        rows = evals.prepare([case()], [{"id":"one", "status":"completed", "text":"Do not send 47 files."}])
        summary = evals.summarize(rows, [{"id":"one", "verdict":"pass", "critique":"Meaning preserved"}], [])
        self.assertEqual(0, summary["human_critical_assessed"])
        self.assertEqual(0, summary["human_cleanup_edit_assessed"])
        self.assertIsNone(summary["human_cleanup_edit_rate"])

    def test_manual_edit_annotation_must_be_boolean_or_null(self):
        with self.assertRaisesRegex(ValueError, "needs_cleanup_edit"):
            evals.validate_judgment({"id":"one", "verdict":"pass", "critique":"Fine", "needs_cleanup_edit":"false"})

    def test_simulated_capture_is_reviewable_but_not_a_real_quality_baseline(self):
        rows = evals.prepare([case()], [{"id":"one", "status":"completed", "text":"Do not send 47 files.",
            "metadata":{"llm":"fake echo endpoint, 0 ms delay, not a real model"}}])
        summary = evals.summarize(rows, [{"id":"one", "verdict":"pass", "critique":"Preserves meaning"}], [])
        self.assertEqual(1, summary["completed"])
        self.assertEqual(0, summary["quality_baseline_eligible"])
        self.assertIsNone(summary["baseline_human_pass_rate"])
        self.assertIn("simulated", rows[0]["baseline_exclusions"][0])

    def test_real_capture_needs_confirmed_required_conditions(self):
        configured = {**case(), "profile":"email", "dictionary":["Rina"]}
        result = {"id":"one", "status":"completed", "text":"Do not send 47 files.",
            "metadata":{"evidence_kind":"real", "cleanup_succeeded":True, "profile_applied":False}}
        row = evals.prepare([configured], [result])[0]
        self.assertFalse(row["quality_baseline_eligible"])
        self.assertIn("Required profile was not confirmed applied", row["baseline_exclusions"])
        self.assertIn("Required dictionary was not confirmed applied", row["baseline_exclusions"])
        result["metadata"].update(profile_applied=True, dictionary_applied=True)
        rows = evals.prepare([configured], [result])
        summary = evals.summarize(rows, [{"id":"one", "verdict":"pass", "critique":"Ready for email"}], [])
        self.assertEqual(1, summary["quality_baseline_eligible"])
        self.assertEqual(1, summary["baseline_human_pass_rate"])

    def test_unidentified_capture_does_not_claim_real_model_evidence(self):
        rows = evals.prepare([case()], [{"id":"one", "status":"completed", "text":"fine"}])
        self.assertFalse(rows[0]["quality_baseline_eligible"])
        self.assertIn("not confirmed", rows[0]["baseline_exclusions"][0])

    def test_real_provider_response_must_reach_selected_cleanup_model(self):
        response = {"text":"Do not send 47 files.", "cleanup_level":"Medium", "profile_id":None,
            "llm_calls":[], "steps":[]}
        row = evals.real_capture_result(case(), response, 10, "google/test", [], "hash")
        self.assertEqual("error", row["status"])
        self.assertIn("Cleanup", row["error"])
        response["llm_calls"] = [{"stage":"Cleanup", "provider":"com.typewhisper.openrouter", "model":"wrong"}]
        self.assertEqual("error", evals.real_capture_result(case(), response, 10, "google/test", [], "hash")["status"])
        response["llm_calls"][0]["model"] = "google/test"
        response["llm_calls"][0]["succeeded"] = True
        self.assertEqual("completed", evals.real_capture_result(case(), response, 10, "google/test", [], "hash")["status"])

    def test_real_capture_confirms_conditions_and_rejects_failed_pipeline_step(self):
        configured = {**case(), "profile":"email", "dictionary":["Rina"]}
        response = {"text":"Draft", "cleanup_level":"Medium", "profile_id":"eval-email",
            "llm_calls":[{"stage":"Cleanup", "provider":"com.typewhisper.openrouter", "model":"google/test", "succeeded":True}],
            "steps":[{"name":"Cleanup", "succeeded":True}]}
        row = evals.real_capture_result(configured, response, 10, "google/test", ["Rina"], "hash")
        self.assertTrue(row["metadata"]["profile_applied"])
        self.assertTrue(row["metadata"]["dictionary_applied"])
        response["steps"][0]["succeeded"] = False
        self.assertEqual("error", evals.real_capture_result(configured, response, 10, "google/test", ["Rina"], "hash")["status"])

    def test_real_capture_rejects_missing_credentials_and_non_openrouter_selection(self):
        for selection, plugin in (("plugin:other:model", {"secret:api-key":"protected"}),
                                  ("plugin:com.typewhisper.openrouter:model", {})):
            with self.assertRaises(ValueError):
                evals.openrouter_capture_config({"defaultLlmProvider":selection, "cleanupLevel":2}, plugin)

    def test_real_attempt_without_success_confirmation_is_not_baseline_evidence(self):
        rows = evals.prepare([case()], [{"id":"one", "status":"completed", "text":"fine",
            "metadata":{"evidence_kind":"real"}}])
        self.assertIn("Successful cleanup response not confirmed", rows[0]["baseline_exclusions"])

    def test_failure_audit_never_returns_raw_error_messages(self):
        audit = evals.cleanup_failure_audit([{"Category":"prompt", "Message":"AI cleanup failed: secret123 model is not a valid model ID"}])
        self.assertEqual(["model_unavailable"], audit)
        self.assertNotIn("secret123", str(audit))


if __name__ == "__main__":
    unittest.main()
