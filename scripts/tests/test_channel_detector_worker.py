from __future__ import annotations

import io
import json
from pathlib import Path
import sys
import unittest
from unittest.mock import patch


SCRIPTS_ROOT = Path(__file__).resolve().parents[1]
if str(SCRIPTS_ROOT) not in sys.path:
    sys.path.insert(0, str(SCRIPTS_ROOT))

import channel_detector_worker as worker


class ChannelDetectorWorkerTests(unittest.TestCase):
    def _report_for(self, outcome_code: str) -> dict[str, object]:
        juice_state, fingerprint_state = worker.OUTCOME_STATES[outcome_code]
        fingerprint_model = "gpt-5.6-sol" if fingerprint_state == "strong_match" else None
        reasons = [] if fingerprint_state == "strong_match" else ["builtin_fingerprint_not_enabled"]
        return {
            "schema_version": worker.REPORT_SCHEMA_VERSION,
            "mode": "single",
            "preset": "low",
            "official": True,
            "official_grade": True,
            "trust_scope": "official_preset",
            "custom_preset": False,
            "operational_status": "complete",
            "verdict_available": True,
            "overall_verdict": "Juice通过；指纹证据不明确",
            "title_cn": "Juice通过；指纹证据不明确",
            "claimed_model": "gpt-5.6-sol",
            "outcome_code": outcome_code,
            "juice_verdict_state": juice_state,
            "fingerprint_verdict_state": fingerprint_state,
            "fingerprint_model": fingerprint_model,
            "fingerprint_summary": {
                "schema_version": worker.REPORT_SCHEMA_VERSION,
                "fingerprint_status": fingerprint_state,
                "fingerprint_model": fingerprint_model,
                "fingerprint_official_eligible": fingerprint_state == "strong_match",
                "fingerprint_unclear_reasons": reasons,
            },
            "juice_summary": {"state": "juice_pass", "valid_completed": 4, "current_success": 4},
            "output_integrity_summary": {"requests": 2, "exact": 2, "hard_anomaly": False},
            "coverage_summary": {"requests": 1, "hard_anomaly": False},
            "network_summary": {
                **worker._empty_network_summary(),
                "logical_tasks": 4,
                "logical_completed": 4,
                "successful": 4,
            },
            "network_error_details": [],
            "retention_complete": True,
            "run_stopped": False,
        }

    def test_main_emits_ordered_safe_lifecycle_events(self) -> None:
        request = {
            "base_url": "https://channel.example.test/v1",
            "model": "gpt-5.6-sol",
            "api_key": "must-not-appear",
            "preset": "low",
        }
        response = worker._summary(
            status="complete",
            model="gpt-5.6-sol",
            official=True,
            error_code=None,
            overall_verdict="通过",
            title_cn="通过",
            network_summary={
                **worker._empty_network_summary(),
                "logical_tasks": 4,
                "logical_completed": 4,
                "successful": 4,
                "http_attempts": 5,
                "retries": 1,
            },
            evidence_summary={
                **worker._empty_evidence_summary(),
                "verdict_available": True,
                "evidence_insufficient": False,
                "juice_state": "juice_pass",
            },
        )
        output = io.StringIO()
        with patch.object(worker, "_read_request", return_value=request), patch.object(
            worker, "run_worker", return_value=response
        ), patch.object(worker.sys, "stdout", output):
            exit_code = worker.main()

        events = [json.loads(line) for line in output.getvalue().splitlines()]
        self.assertEqual(exit_code, 0)
        self.assertEqual([item["event"] for item in events], ["probe.started", "probe.completed"])
        self.assertEqual(events[0]["model"], "gpt-5.6-sol")
        self.assertEqual(events[1]["summary"]["network_summary"]["http_attempts"], 5)
        self._assert_safe_keys(events)
        self.assertNotIn("must-not-appear", output.getvalue())

    def test_safe_report_mapping_reduces_network_errors_to_counts(self) -> None:
        report = {
            "network_summary": {
                "logical_tasks": 3,
                "logical_completed": 2,
                "successful": 1,
                "final_errors": 1,
                "http_attempts": 4,
            },
            "network_error_details": [
                {"category": "request timeout", "raw_response": "must-not-appear"},
                {"category": "transport connection", "request_headers": {"authorization": "secret"}},
            ],
        }

        summary = worker._safe_network_summary(report)

        self.assertEqual(summary["error_categories"], {"network_error": 1, "timeout": 1})
        self.assertNotIn("must-not-appear", json.dumps(summary))
        self._assert_safe_keys(summary)

    def test_schema_three_accepts_all_seven_outcomes(self) -> None:
        for outcome_code in sorted(worker.OUTCOME_CODES):
            with self.subTest(outcome_code=outcome_code):
                report = self._report_for(outcome_code)
                evidence = worker._safe_evidence_summary(report)
                self.assertIsNone(
                    worker._validate_report(report, "gpt-5.6-sol", report["network_summary"]),
                )
                self.assertEqual(evidence["outcome_code"], outcome_code)
                self.assertEqual(evidence["hard_verdict"], outcome_code in worker.HARD_OUTCOME_CODES)
                self.assertEqual(
                    evidence["evidence_insufficient"],
                    outcome_code.startswith("juice_insufficient_"),
                )
                self._assert_safe_keys(evidence)

    def test_low_preset_fingerprint_unclear_is_not_hard_failure(self) -> None:
        report = self._report_for("juice_pass_fingerprint_unclear")

        evidence = worker._safe_evidence_summary(report)

        self.assertEqual(evidence["juice_state"], "pass")
        self.assertEqual(evidence["fingerprint_state"], "unclear")
        self.assertFalse(evidence["fingerprint_enabled"])
        self.assertFalse(evidence["hard_verdict"])
        self.assertFalse(evidence["evidence_insufficient"])

    def test_unknown_schema_and_conflicting_fields_are_rejected(self) -> None:
        unknown_schema = self._report_for("juice_pass_fingerprint_unclear")
        unknown_schema["schema_version"] = 2
        conflicting = self._report_for("juice_mismatch_fingerprint_unclear")
        conflicting["juice_verdict_state"] = "pass"

        self.assertEqual(
            worker._validate_report(unknown_schema, "gpt-5.6-sol", unknown_schema["network_summary"]),
            "unsupported_schema",
        )
        self.assertEqual(
            worker._validate_report(conflicting, "gpt-5.6-sol", conflicting["network_summary"]),
            "evidence_insufficient",
        )

    def test_exception_messages_never_cross_the_worker_boundary(self) -> None:
        with patch.dict(sys.modules, {"gpt56_vnext.detector": None}):
            response = worker.run_worker(
                {
                    "base_url": "https://channel.example.test/v1",
                    "model": "gpt-5.6-terra",
                    "api_key": "secret-value",
                    "preset": "low",
                }
            )

        serialized = json.dumps(response, ensure_ascii=False)
        self.assertEqual(response["status"], "error")
        self.assertNotIn("secret-value", serialized)
        self.assertNotIn("No module", serialized)
        self._assert_safe_keys(response)

    def _assert_safe_keys(self, value: object) -> None:
        forbidden = {
            "api_key",
            "token",
            "prompt",
            "request",
            "response",
            "raw_request",
            "raw_response",
            "request_body",
            "response_body",
            "request_headers",
            "response_headers",
            "headers",
            "authorization",
            "cookie",
            "path",
            "stderr",
            "traceback",
        }

        def visit(item: object) -> None:
            if isinstance(item, dict):
                for key, child in item.items():
                    normalized = str(key).casefold()
                    self.assertFalse(
                        normalized in forbidden,
                        f"forbidden worker field: {key}",
                    )
                    visit(child)
            elif isinstance(item, list):
                for child in item:
                    visit(child)

        visit(value)


if __name__ == "__main__":
    unittest.main()
