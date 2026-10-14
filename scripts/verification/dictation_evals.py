#!/usr/bin/env python3
"""Prepare local dictation review packets and compare human and optional LLM judgments."""
import argparse
from collections import Counter
from datetime import datetime, timezone
import html
import hashlib
import json
import os
from pathlib import Path
import re
import shutil
import sys
import tempfile
import time
import urllib.request

ROOT = Path(__file__).resolve().parents[2]
DEFAULT_CASES = ROOT / "evals/dictation/cases.jsonl"
JUDGE_PROMPT = ROOT / "evals/dictation/judge-system.txt"
VERDICTS = {"pass", "fail", "uncertain"}


def read_rows(path):
    rows = [json.loads(line) for line in Path(path).read_text().splitlines() if line.strip()]
    indexed(rows)
    return rows


def indexed(rows):
    result = {}
    for row in rows:
        identifier = row.get("id")
        if not isinstance(identifier, str) or not identifier:
            raise ValueError("Every row needs a nonempty string id")
        if identifier in result:
            raise ValueError("duplicate id: " + identifier)
        result[identifier] = row
    return result


def contains_term(text, term):
    return re.search(r"(?<!\w)" + re.escape(term) + r"(?!\w)", text, re.IGNORECASE) is not None


def prepare(cases, results):
    known, observed = indexed(cases), indexed(results)
    if observed.keys() - known.keys():
        raise ValueError("unknown result ids: " + ", ".join(sorted(observed.keys() - known.keys())))
    rows = []
    for case in cases:
        result = observed.get(case["id"], {})
        status = result.get("status", "pending")
        if status not in {"pending", "completed", "error"}:
            raise ValueError("Invalid result status for " + case["id"])
        text = result.get("text")
        if status == "completed" and not isinstance(text, str):
            raise ValueError("Completed result needs text, including an explicit empty string")
        before = result.get("raw_text", case["input"])
        if not isinstance(before, str):
            raise ValueError("raw_text must be a string")
        metadata = result.get("metadata", {})
        if not isinstance(metadata, dict):
            raise ValueError("metadata must be an object")
        exclusions = []
        if status != "completed":
            exclusions.append("Output not captured")
        # Recognize the shared capture runner's initial fixture label as well as explicit new metadata.
        simulated = metadata.get("evidence_kind") == "simulated" or str(metadata.get("llm", "")).startswith("fake echo endpoint,")
        if simulated:
            exclusions.append("Provider output is simulated; not a real-model baseline")
        elif metadata.get("evidence_kind") != "real":
            exclusions.append("Real-provider evidence not confirmed")
        elif metadata.get("cleanup_succeeded") is not True:
            exclusions.append("Successful cleanup response not confirmed")
        if case.get("profile", "plain") != "plain" and metadata.get("profile_applied") is not True:
            exclusions.append("Required profile was not confirmed applied")
        if case.get("dictionary") and metadata.get("dictionary_applied") is not True:
            exclusions.append("Required dictionary was not confirmed applied")
        rows.append({**case, "status": status, "before": before,
            "input_changed": before != case["input"], "after": text if status == "completed" else None,
            "metadata": metadata, "error": result.get("error"),
            "quality_baseline_eligible": not exclusions, "baseline_exclusions": exclusions,
            "missing_terms": [term for term in case.get("required_terms", []) if status == "completed" and not contains_term(text, term)],
            "unexpected_terms": [term for term in case.get("forbidden_terms", []) if status == "completed" and contains_term(text, term)]})
    return rows


def validate_judgment(row):
    if row.get("verdict") not in VERDICTS or not isinstance(row.get("critique"), str) or not row["critique"].strip():
        raise ValueError("Judgments need pass/fail/uncertain and a written critique")
    errors = row.get("critical_errors", [])
    if not isinstance(errors, list) or any(not isinstance(error, str) for error in errors):
        raise ValueError("critical_errors must be an array of strings")
    if errors and row["verdict"] != "fail":
        raise ValueError("A critical error cannot receive a pass or uncertain verdict")
    criteria = row.get("criteria", {})
    if not isinstance(criteria, dict) or any(verdict not in VERDICTS for verdict in criteria.values()):
        raise ValueError("Invalid criterion verdict")
    if row["verdict"] == "pass" and any(verdict != "pass" for verdict in criteria.values()):
        raise ValueError("A failed or uncertain criterion cannot receive an overall pass")
    edit = row.get("needs_cleanup_edit")
    if edit is not None and not isinstance(edit, bool):
        raise ValueError("needs_cleanup_edit must be boolean or null")
    return row


def summarize(rows, human, judge):
    completed = {row["id"] for row in rows if row["status"] == "completed"}
    all_humans, judges = indexed(human), indexed(judge)
    if all_humans.keys() - completed:
        raise ValueError("Labels must refer to completed case ids")
    humans = {key: row for key, row in all_humans.items() if row.get("verdict") != "pending"}
    for labels in (humans, judges):
        if labels.keys() - completed:
            raise ValueError("Labels must refer to completed case ids")
        for label in labels.values(): validate_judgment(label)
    definite_human = {key: row for key, row in humans.items() if row["verdict"] != "uncertain"}
    baseline_ids = {row["id"] for row in rows if row.get("quality_baseline_eligible") is True}
    baseline_labels = [row for key, row in definite_human.items() if key in baseline_ids]
    critical_assessed = [row for row in humans.values() if "critical_errors" in row]
    edit_assessed = [row for row in humans.values() if isinstance(row.get("needs_cleanup_edit"), bool)]
    edits_needed = sum(row["needs_cleanup_edit"] for row in edit_assessed)
    paired = [key for key in definite_human if key in judges and judges[key]["verdict"] != "uncertain"]
    by_category = {}
    for category in sorted({row["category"] for row in rows}):
        ids = {row["id"] for row in rows if row["category"] == category}
        labels = [humans[key] for key in ids if key in humans]
        passed = sum(label["verdict"] == "pass" for label in labels)
        failed = sum(label["verdict"] == "fail" for label in labels)
        by_category[category] = {"cases":len(ids), "reviewed":len(labels), "passed":passed, "failed":failed,
            "uncertain":len(labels)-passed-failed, "pass_rate":passed/(passed+failed) if passed+failed else None}
    return {"total_cases": len(rows), "completed": len(completed),
        "quality_baseline_eligible": len(baseline_ids),
        "baseline_exclusions": {row["id"]:row.get("baseline_exclusions", ["Regenerate review to confirm evidence and conditions"])
            for row in rows if row["id"] not in baseline_ids},
        "baseline_human_definite_reviews": len(baseline_labels),
        "baseline_human_pass_rate": sum(row["verdict"] == "pass" for row in baseline_labels) / len(baseline_labels) if baseline_labels else None,
        "pending": sum(row["status"] == "pending" for row in rows),
        "errors": sum(row["status"] == "error" for row in rows),
        "categories": dict(Counter(row["category"] for row in rows)),
        "human_by_category": by_category,
        "term_warning_ids": [row["id"] for row in rows if row["missing_terms"] or row["unexpected_terms"]],
        "human_reviewed": len(humans), "human_uncertain": sum(row["verdict"] == "uncertain" for row in humans.values()),
        "human_critical_assessed": len(critical_assessed),
        "human_critical_failure_ids": [row["id"] for row in critical_assessed if row["critical_errors"]],
        "human_cleanup_edit_assessed": len(edit_assessed),
        "human_cleanup_edits_needed": edits_needed,
        "human_cleanup_edit_rate": edits_needed / len(edit_assessed) if edit_assessed else None,
        "human_pass_rate": sum(row["verdict"] == "pass" for row in definite_human.values()) / len(definite_human) if definite_human else None,
        "judge_reviewed": len(judges), "judge_uncertain": sum(row["verdict"] == "uncertain" for row in judge),
        "paired_definite_reviews": len(paired),
        "judge_human_agreement": sum(humans[key]["verdict"] == judges[key]["verdict"] for key in paired) / len(paired) if paired else None,
        "judge_false_pass_ids": [key for key in paired if humans[key]["verdict"] == "fail" and judges[key]["verdict"] == "pass"],
        "disagreement_ids": [key for key in paired if humans[key]["verdict"] != judges[key]["verdict"]],
        "judge_status": "Advisory. Agreement on reviewed cases is not proof of calibration or a release gate."}


def render_review(rows):
    blocks = []
    for row in rows:
        escape = lambda value: html.escape(str(value))
        after = row["after"] if row["after"] is not None else "Not run"
        warnings = {"missing_terms": row["missing_terms"], "unexpected_terms": row["unexpected_terms"], "input_changed": row["input_changed"]}
        exclusions = row.get("baseline_exclusions", ["Regenerate review to confirm evidence and conditions"])
        eligibility = '<p><strong>Excluded from real quality baseline:</strong> ' + escape("; ".join(exclusions)) + '</p>' if exclusions else '<p>Eligible for real quality baseline review.</p>'
        blocks.append(f'<article><h2>{escape(row["id"])} <small>{escape(row["status"])}</small></h2>'
            f'{eligibility}<p>{escape(row["expected_behavior"])}</p><div class="pair"><section><h3>Before</h3><pre>{escape(row["before"])}</pre></section>'
            f'<section><h3>After</h3><pre>{escape(after)}</pre></section></div>'
            f'<details><summary>Example, context, and diagnostics</summary><pre>{escape(json.dumps({"example_output":row["example_output"], "language":row.get("language"), "profile":row.get("profile", "plain"), "dictionary":row.get("dictionary", []), "diagnostics":warnings, "metadata":row["metadata"]}, indent=2))}</pre></details></article>')
    return '<!doctype html><html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1"><title>Dictation eval review</title><style>body{font:15px system-ui;max-width:1000px;margin:24px auto;padding:0 16px;color:var(--foreground,#202020)}article{border-top:1px solid var(--border,#bbb);padding:18px 0}h2{font-size:18px}h3{font-size:14px}small{font-weight:normal;color:var(--muted-foreground,#666)}.pair{display:grid;grid-template-columns:1fr 1fr;gap:20px}pre{white-space:pre-wrap;overflow-wrap:anywhere;background:var(--muted,#f3f3f3);padding:12px}details{margin-top:12px}@media(max-width:600px){.pair{grid-template-columns:1fr}}</style></head><body><h1>Dictation eval review</h1><p>Compare meaning before fluency. Completed means captured, not passed. Missing outputs are pending. Term warnings are diagnostics, not verdicts. Examples permit equivalent wording. Check metadata for authored demonstration outputs.</p>' + "".join(blocks) + '</body></html>'


def write_jsonl(path, rows):
    Path(path).write_text("".join(json.dumps(row, ensure_ascii=False) + "\n" for row in rows))


def judge_packet(row):
    # Hide the generator's model identity from the judge to reduce model bias.
    return {key: row.get(key) for key in ["id", "input", "before", "after", "expected_behavior", "example_output", "language", "profile", "dictionary", "missing_terms", "unexpected_terms", "input_changed"]}


def openrouter_capture_config(settings, plugin):
    selection = settings.get("defaultLlmProvider", "")
    prefix = "plugin:com.typewhisper.openrouter:"
    if not selection.startswith(prefix) or not selection[len(prefix):]:
        raise ValueError("The selected cleanup provider must be OpenRouter")
    if not plugin.get("secret:api-key"):
        raise ValueError("The existing OpenRouter account has no stored credential")
    if settings.get("cleanupLevel") not in (2, 3):
        raise ValueError("Select Medium or High cleanup for a real LLM baseline")
    keys = ("cleanupLevel", "cleanupTimeoutSeconds", "spokenFormattingStrategy", "spokenFormattingProfiles",
            "transcriptionNumberNormalizationEnabled", "englishOutputVariant", "germanOutputVariant",
            "shortUtterancePunctuationEnabled")
    overrides = {key:settings[key] for key in keys if key in settings}
    overrides.update(defaultLlmProvider=selection, memoryEnabled=False, commandModeEnabled=False,
        pluginEnabledState={"com.typewhisper.openrouter":True, "com.typewhisper.filler-words":False})
    return overrides, selection[len(prefix):]


def real_capture_result(case, response, latency_ms, model, terms, prompt_hash):
    call = next((item for item in response.get("llm_calls", []) if item.get("stage") == "Cleanup"), None)
    error = None
    if call is None:
        error = "No Cleanup LLM call recorded; requested cleanup may have fallen back to Light"
    elif call.get("provider") != "com.typewhisper.openrouter" or call.get("model") != model:
        error = "Cleanup did not use the selected OpenRouter model"
    elif call.get("succeeded") is not True:
        error = "Cleanup did not confirm a nonempty completed LLM response"
    elif any(step.get("succeeded") is not True for step in response.get("steps", [])):
        error = "A text processing step failed"
    profile = case.get("profile", "plain")
    profile_id = None if profile == "plain" else "eval-" + profile
    return {"id":case["id"], "status":"error" if error else "completed", "raw_text":response.get("raw_text", case["input"]),
        "text":None if error else response["text"], "error":error,
        "metadata":{"evidence_kind":"real", "capture_route":"/v1/text/process", "transcription_model":None,
            "backend":None, "cleanup_provider":call.get("provider") if call else None,
            "cleanup_model":call.get("model") if call else None, "cleanup_level":response.get("cleanup_level"),
            "cleanup_succeeded":call.get("succeeded") if call else False,
            "prompt_version":prompt_hash, "profile":profile, "profile_id":response.get("profile_id"),
            "profile_applied":response.get("profile_id") == profile_id,
            "dictionary_applied":set(case.get("dictionary", [])) <= set(terms),
            "dictionary_version":hashlib.sha256(json.dumps(sorted(terms)).encode()).hexdigest(),
            "dictionary_scope":"Union of authored case terms in the isolated app", "audio_case":None,
            "latency_ms":latency_ms, "steps":response.get("steps", []), "llm_calls":response.get("llm_calls", [])}}


def cleanup_failure_audit(entries):
    """Return safe failure classifications, never provider bodies or credential-bearing messages."""
    failures = []
    for entry in entries:
        message = str(entry.get("Message", ""))
        if "cleanup failed" not in message.lower() and "cleanup exceeded" not in message.lower():
            continue
        if re.search(r"not a valid model|model.*not found|no endpoints found", message, re.IGNORECASE):
            failures.append("model_unavailable")
        elif re.search(r"unauthorized|invalid.*key|401|403", message, re.IGNORECASE):
            failures.append("authentication_failed")
        elif "exceeded" in message.lower():
            failures.append("cleanup_timeout")
        else:
            failures.append("cleanup_provider_failed")
    return failures


def capture_real(args):
    """Use a private temporary credential copy; keep only authored results and safe metadata."""
    sys.path.insert(0, str(ROOT / "scripts/verification"))
    import isolated_app
    if not args.use_existing_openrouter:
        raise ValueError("Real capture requires explicit --use-existing-openrouter")
    if args.out.exists() or args.out.with_name(args.out.stem + "-run.json").exists():
        raise ValueError("Capture output exists; choose a new run path")
    cases = read_rows(args.cases)
    user_data = isolated_app.USER_DATA
    settings = json.loads((user_data / "settings.json").read_text())
    plugin = json.loads((user_data / "PluginData/com.typewhisper.openrouter/settings.json").read_text())
    overrides, model = openrouter_capture_config(settings, plugin)
    key_path = user_data / "secret-protection.key"
    if not key_path.is_file():
        raise ValueError("Stored credential protection key is missing")
    prompt_hash = hashlib.sha256((ROOT / "src/TypeWhisper.Core/Services/CleanupService.cs").read_bytes()).hexdigest()
    terms = sorted({term for case in cases for term in case.get("dictionary", [])})
    presets = {"email":4, "chat":5, "developer":6, "notes":7}
    profiles = []
    for name in sorted({case.get("profile", "plain") for case in cases} - {"plain"}):
        if name not in presets:
            raise ValueError("No authored isolated profile mapping for " + name)
        profiles.append({"Id":"eval-"+name, "Name":"Eval "+name, "IsEnabled":True,
            "ProcessNames":["typewhisper-eval-"+name], "StylePreset":presets[name],
            "CleanupLevelOverride":overrides["cleanupLevel"], "DeveloperFormattingOverride":name == "developer"})
    rows = []
    args.out.parent.mkdir(parents=True, exist_ok=True)
    # TemporaryDirectory is owner-only and deletes the copied credential, key, backups, and logs on exit.
    with tempfile.TemporaryDirectory(prefix="tw-real-eval-") as private_dir:
        app = isolated_app.IsolatedApp(Path(private_dir), configuration="Release", settings=overrides,
            plugin_settings={"com.typewhisper.openrouter":plugin})
        data = app.instance / "data/TypeWhisper"
        (data / "Data").mkdir(parents=True)
        shutil.copyfile(key_path, data / "secret-protection.key")
        os.chmod(data / "secret-protection.key", 0o600)
        isolated_app.write_json(data / "Data/profiles.json", profiles)
        with app:
            request = urllib.request.Request(f"http://127.0.0.1:{app.port}/v1/dictionary/terms",
                data=json.dumps({"terms":terms, "replace":True}).encode(), method="PUT",
                headers={"Authorization":"Bearer "+app.token, "Content-Type":"application/json"})
            with urllib.request.urlopen(request, timeout=10) as response:
                applied_terms = json.load(response)["terms"]
            # API readiness precedes provider registration. No paid canary is needed.
            time.sleep(2)
            with args.out.open("x") as output:
                for case in cases:
                    profile = case.get("profile", "plain")
                    payload = {"text":case["input"], "language":case.get("language")}
                    if profile != "plain": payload["profile_id"] = "eval-" + profile
                    started = time.perf_counter()
                    try:
                        response = app.api("/v1/text/process", payload)
                        row = real_capture_result(case, response, (time.perf_counter()-started)*1000, model, applied_terms, prompt_hash)
                        errors_path = data / "Data/error-log.json"
                        failures = cleanup_failure_audit(json.loads(errors_path.read_text())) if errors_path.exists() else []
                        row["metadata"]["isolated_failure_audit"] = failures
                        if failures:
                            row.update(status="error", text=None, error="Isolated cleanup failure audit: " + ", ".join(failures))
                            row["metadata"]["cleanup_succeeded"] = False
                    except Exception as error:
                        # Never retain response bodies or exception messages that might contain credentials.
                        row = {"id":case["id"], "status":"error", "raw_text":case["input"], "text":None,
                            "error":type(error).__name__, "metadata":{"evidence_kind":"real"}}
                    rows.append(row)
                    output.write(json.dumps(row, ensure_ascii=False) + "\n")
                    output.flush()
                    print(case["id"] + ": " + row["status"], flush=True)
    manifest = isolated_app.run_manifest({"kind":"real-text-eval-capture", "case_count":len(cases),
        "case_set_sha256":hashlib.sha256(args.cases.read_bytes()).hexdigest(), "cleanup_model":model,
        "cleanup_settings":overrides, "prompt_source_sha256":prompt_hash, "profiles":profiles,
        "dictionary_terms":terms, "audio_transcription_performed":False,
        "release_assembly_sha256":hashlib.sha256(isolated_app.app_assembly("Release").read_bytes()).hexdigest()})
    isolated_app.write_json(args.out.with_name(args.out.stem + "-run.json"), manifest)
    failures = [row["id"] for row in rows if row["status"] != "completed"]
    print(json.dumps({"completed":len(rows)-len(failures), "errors":failures}))
    if failures: raise SystemExit("Some real captures failed; successful results were retained")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--cases", type=Path, default=DEFAULT_CASES)
    sub = parser.add_subparsers(dest="command", required=True)
    template = sub.add_parser("template")
    template.add_argument("--out", type=Path, required=True)
    review = sub.add_parser("review")
    review.add_argument("--results", type=Path, required=True)
    review.add_argument("--out", type=Path, required=True)
    score = sub.add_parser("score")
    score.add_argument("--review", type=Path, required=True)
    score.add_argument("--human", type=Path)
    score.add_argument("--judge", type=Path)
    score.add_argument("--out", type=Path, required=True)
    judge = sub.add_parser("judge")
    judge.add_argument("--review", type=Path, required=True)
    judge.add_argument("--endpoint", required=True, help="Full OpenAI-compatible chat completions URL")
    judge.add_argument("--model", required=True)
    judge.add_argument("--key-env", default="DICTATION_JUDGE_API_KEY")
    judge.add_argument("--out", type=Path, required=True)
    capture = sub.add_parser("capture-real", help="Explicit opt-in: use existing OpenRouter credentials in a private isolated Release app")
    capture.add_argument("--use-existing-openrouter", action="store_true")
    capture.add_argument("--out", type=Path, required=True)
    args = parser.parse_args()
    if args.command == "template":
        if args.out.exists(): raise ValueError("Results file exists; choose a new path to preserve captured outputs")
        args.out.parent.mkdir(parents=True, exist_ok=True)
        write_jsonl(args.out, [{"id":case["id"], "status":"pending", "raw_text":case["input"], "text":None,
            "metadata":{"transcription_model":None, "backend":None, "cleanup_provider":None, "cleanup_model":None,
            "cleanup_level":None, "prompt_version":None, "profile":case.get("profile","plain"), "dictionary_version":None,
            "audio_case":None, "latency_ms":None}} for case in read_rows(args.cases)])
    elif args.command == "review":
        if (args.out / "human-labels.jsonl").exists(): raise ValueError("Review directory exists; choose a new path to preserve human labels")
        rows = prepare(read_rows(args.cases), read_rows(args.results))
        args.out.mkdir(parents=True, exist_ok=True)
        write_jsonl(args.out / "review.jsonl", rows)
        write_jsonl(args.out / "judge-packets.jsonl", [judge_packet(row) for row in rows if row["status"] == "completed"])
        write_jsonl(args.out / "human-labels.jsonl", [{"id":row["id"],"verdict":"pending","critique":"", "needs_cleanup_edit":None} for row in rows if row["status"] == "completed"])
        (args.out / "review.html").write_text(render_review(rows))
        (args.out / "summary.json").write_text(json.dumps(summarize(rows, [], []), indent=2))
    elif args.command == "score":
        summary = summarize(read_rows(args.review), read_rows(args.human) if args.human else [], read_rows(args.judge) if args.judge else [])
        args.out.parent.mkdir(parents=True, exist_ok=True)
        args.out.write_text(json.dumps(summary, indent=2))
        print(json.dumps(summary, indent=2))
    elif args.command == "capture-real":
        capture_real(args)
    else:
        rows = [row for row in read_rows(args.review) if row["status"] == "completed"]
        key = os.environ.get(args.key_env)
        if not rows: raise ValueError("No completed cases to judge")
        if not key: raise ValueError("Missing judge key environment variable: " + args.key_env)
        prompt = JUDGE_PROMPT.read_text()
        prompt_hash = hashlib.sha256(prompt.encode()).hexdigest()
        args.out.parent.mkdir(parents=True, exist_ok=True)
        if args.out.exists(): raise ValueError("Judge output already exists; use a new run path")
        failures = []
        with args.out.open("w") as output:
            for row in rows:
                try:
                    payload = {"model":args.model, "temperature":0, "messages":[{"role":"system","content":prompt},
                        {"role":"user","content":json.dumps(judge_packet(row),ensure_ascii=False)}]}
                    request = urllib.request.Request(args.endpoint, data=json.dumps(payload).encode(),
                        headers={"Authorization":"Bearer " + key,"Content-Type":"application/json"})
                    with urllib.request.urlopen(request, timeout=60) as response: body = json.load(response)
                    judgment = validate_judgment(json.loads(body["choices"][0]["message"]["content"]))
                    if set(judgment.get("criteria", {})) != {"meaning", "disfluencies", "terms_and_literals", "profile_and_readability"}:
                        raise ValueError("Judge omitted rubric criteria")
                    if judgment.get("id") != row["id"]: raise ValueError("Judge returned a different case id")
                    judgment["judge_metadata"] = {"requested_model":args.model,"returned_model":body.get("model"),
                        "usage":body.get("usage"),"system_prompt_sha256":prompt_hash,
                        "timestamp_utc":datetime.now(timezone.utc).isoformat()}
                    output.write(json.dumps(judgment,ensure_ascii=False) + "\n")
                    output.flush()
                except Exception as error:
                    # Do not save HTTP bodies or exception text that can expose credentials.
                    failures.append({"id":row["id"],"error_type":type(error).__name__})
        if failures:
            args.out.with_suffix(".errors.json").write_text(json.dumps(failures,indent=2))
            raise SystemExit("Some judgments failed; partial output and error types were saved. No failed case was scored as a pass.")


if __name__ == "__main__":
    main()
