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
