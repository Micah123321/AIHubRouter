"""Run one official gpt56_vnext detector session through a safe stdin/stdout protocol.

The worker deliberately keeps the detector's runtime state in a temporary directory.  The
only persistent output is a small, allow-listed JSON summary; request credentials and raw
probe data never cross that boundary.
"""

from __future__ import annotations

from collections import Counter
import json
import sys
from pathlib import Path
import tempfile
from typing import Any
from urllib.parse import urlsplit


REFERENCE_ROOT = Path(__file__).resolve().parents[1] / "gpt56_api_detector"
SUPPORTED_MODELS = frozenset({"gpt-5.6-sol", "gpt-5.6-terra", "gpt-5.6-luna"})
VERDICT_TITLES = frozenset(
    {
        "可能非GPT",
        "Juice混用",
        "仅概率探针混用",
        "Juice通过但概率探针证据不足",
        "通过",
    }
)
HARD_VERDICTS = frozenset({"可能非GPT", "Juice混用", "仅概率探针混用"})
MAX_INPUT_BYTES = 1_048_576


def _empty_network_summary() -> dict[str, Any]:
    return {
        "logical_tasks": 0,
        "logical_completed": 0,
        "successful": 0,
        "final_errors": 0,
        "cancelled": 0,
        "http_attempts": 0,
        "retries": 0,
        "in_flight": 0,
        "error_categories": {},
    }


def _empty_evidence_summary() -> dict[str, Any]:
    return {
        "verdict_available": False,
        "hard_verdict": False,
        "juice_state": "unknown",
        "juice_valid_completed": 0,
        "juice_current_success": 0,
        "juice_mixed": 0,
        "juice_network_errors": 0,
        "output_requests": 0,
        "output_exact": 0,
        "coverage_requests": 0,
        "coverage_hard_anomaly": False,
        "probability_enabled": False,
        "probability_formal_eligible": None,
        "evidence_insufficient": True,
    }


def _summary(
    *,
    status: str,
    model: str | None,
    official: bool,
    error_code: str | None,
    overall_verdict: str | None = None,
    title_cn: str = "未形成正式结论",
    network_summary: dict[str, Any] | None = None,
    evidence_summary: dict[str, Any] | None = None,
) -> dict[str, Any]:
    """Build the fixed, allow-listed worker response shape."""

    return {
        "status": status,
        "overall_verdict": overall_verdict,
        "title_cn": title_cn if overall_verdict is not None else "未形成正式结论",
        "official": bool(official),
        "claimed_model": model if model in SUPPORTED_MODELS else None,
        "network_summary": network_summary or _empty_network_summary(),
        "evidence_summary": evidence_summary or _empty_evidence_summary(),
        "error_code": error_code,
    }


def _event(event_type: str, *, model: str | None, status: str, summary: dict[str, Any] | None = None) -> dict[str, Any]:
    """Build a lifecycle event without exposing request or detector data."""

    event = {
        "event": event_type,
        "status": status,
        "model": model if model in SUPPORTED_MODELS else None,
    }
    if summary is not None:
        event["summary"] = summary
    return event


def _integer(value: Any) -> int:
    if isinstance(value, bool):
        return 0
    try:
        return max(0, int(value))
    except (TypeError, ValueError, OverflowError):
        return 0


def _safe_network_summary(report: dict[str, Any]) -> dict[str, Any]:
    source = report.get("network_summary")
    source = source if isinstance(source, dict) else {}
    summary = _empty_network_summary()
    for key in (
        "logical_tasks",
        "logical_completed",
        "successful",
        "final_errors",
        "cancelled",
        "http_attempts",
        "retries",
        "in_flight",
    ):
        summary[key] = _integer(source.get(key))

    categories: Counter[str] = Counter()
    details = report.get("network_error_details")
    if isinstance(details, list):
        for item in details:
            if not isinstance(item, dict):
                continue
            category = _normalize_error_category(item.get("category"))
            if category is not None:
                categories[category] += 1
    summary["error_categories"] = dict(sorted(categories.items()))
    return summary


def _safe_evidence_summary(report: dict[str, Any]) -> dict[str, Any]:
    juice = report.get("juice_summary")
    juice = juice if isinstance(juice, dict) else {}
    output = report.get("output_integrity_summary")
    output = output if isinstance(output, dict) else {}
    coverage = report.get("coverage_summary")
    coverage = coverage if isinstance(coverage, dict) else {}
    completeness = report.get("data_completeness")
    completeness = completeness if isinstance(completeness, dict) else {}
    probability_completeness = completeness.get("probability")
    probability_completeness = (
        probability_completeness if isinstance(probability_completeness, dict) else {}
    )

    verdict = report.get("overall_verdict")
    summary = _empty_evidence_summary()
    probability_enabled = probability_completeness.get("enabled")
    if not isinstance(probability_enabled, bool):
        probability_enabled = False
    formal_eligible = probability_completeness.get("formal_eligible")
    if not isinstance(formal_eligible, bool):
        formal_eligible = None
    summary.update(
        {
            "verdict_available": verdict in VERDICT_TITLES,
            "hard_verdict": verdict in HARD_VERDICTS,
            "juice_state": juice.get("state") if juice.get("state") in {
                "juice_pass",
                "juice_mixed",
                "juice_all_unsuccessful",
                "data_insufficient",
            } else "unknown",
            "juice_valid_completed": _integer(juice.get("valid_completed")),
            "juice_current_success": _integer(juice.get("current_success")),
            "juice_mixed": _integer(juice.get("mixed")),
            "juice_network_errors": _integer(juice.get("network_errors")),
            "output_requests": _integer(output.get("requests")),
            "output_exact": _integer(output.get("exact")),
            "coverage_requests": _integer(coverage.get("requests")),
            "coverage_hard_anomaly": bool(coverage.get("hard_anomaly")),
            "probability_enabled": probability_enabled,
            "probability_formal_eligible": formal_eligible,
            "evidence_insufficient": not (verdict in VERDICT_TITLES),
        }
    )
    return summary


def _normalize_error_category(category: Any) -> str | None:
    """Map reference detector categories to a small stable public vocabulary."""

    value = str(category or "").casefold()
    if not value:
        return None
    if "http" in value:
        return "http_error"
    if "timeout" in value or "timed out" in value:
        return "timeout"
    if "truncated" in value or "invalid" in value or "sse" in value:
        return "truncated_stream"
    if "network" in value or "transport" in value or "connection" in value:
        return "network_error"
    return "processing_error"


def _error_code_from_report(report: dict[str, Any]) -> str | None:
    details = report.get("network_error_details")
    categories: set[str] = set()
    if isinstance(details, list):
        for item in details:
            if isinstance(item, dict):
                normalized = _normalize_error_category(item.get("category"))
                if normalized:
                    categories.add(normalized)
    if not categories:
        network = report.get("network_summary")
        if isinstance(network, dict) and _integer(network.get("final_errors")):
            return "processing_error"
        return None
    for candidate in ("http_error", "timeout", "truncated_stream", "network_error", "processing_error"):
        if candidate in categories:
            return candidate
    return "processing_error"


def _error_code_from_exception(exc: BaseException) -> str:
    value = str(exc).casefold()
    name = type(exc).__name__.casefold()
    if "timeout" in value or "timeout" in name:
        return "timeout"
    if "http" in value or "http" in name:
        return "http_error"
    if "sse" in value or "stream" in value or "truncated" in value:
        return "truncated_stream"
    if "url" in name or "socket" in name or "connection" in value or "transport" in value:
        return "network_error"
    return "processing_error"


def _validate_input(value: Any) -> tuple[str, str, str]:
    if not isinstance(value, dict):
        raise ValueError("input must be an object")
    required = ("base_url", "model", "api_key", "preset")
    if any(not isinstance(value.get(key), str) for key in required):
        raise ValueError("input fields must be strings")
    base_url = value["base_url"].strip()
    model = value["model"].strip()
    api_key = value["api_key"]
    preset = value["preset"].strip()
    if not base_url or not api_key:
        raise ValueError("base_url, model, and api_key are required")
    if model not in SUPPORTED_MODELS:
        raise ValueError("unsupported model")
    if preset != "low":
        raise ValueError("only the official low preset is supported")
    parsed = urlsplit(base_url)
    if parsed.scheme not in {"http", "https"} or not parsed.hostname:
        raise ValueError("base_url must be an HTTP(S) URL")
    return base_url, model, api_key


def _read_request() -> Any:
    raw = sys.stdin.buffer.readline(MAX_INPUT_BYTES + 1)
    if not raw or len(raw) > MAX_INPUT_BYTES:
        raise ValueError("stdin must contain one JSON line")
    try:
        return json.loads(raw.decode("utf-8"))
    except (UnicodeDecodeError, json.JSONDecodeError) as exc:
        raise ValueError("stdin is not valid JSON") from exc


def run_worker(request: Any) -> dict[str, Any]:
    """Execute one low official session and return only the safe summary."""

    try:
        base_url, model, api_key = _validate_input(request)
    except ValueError:
        return _summary(status="error", model=None, official=False, error_code="invalid_input")

    session = None
    try:
        if str(REFERENCE_ROOT) not in sys.path:
            sys.path.insert(0, str(REFERENCE_ROOT))
        from gpt56_vnext.detector import DetectorSession
        from gpt56_vnext.presets import get_preset

        config = get_preset("single", "low")
        # TemporaryDirectory owns all SQLite/WAL files produced by DetectorSession.
        with tempfile.TemporaryDirectory(prefix="channel-detector-") as runtime_directory:
            session = DetectorSession(
                base_url=base_url,
                model=model,
                api_key=api_key,
                config=config,
                directory=runtime_directory,
            )
            try:
                report = session.run_single()
            finally:
                session.close()
                session = None
    except BaseException as exc:
        if session is not None:
            try:
                session.close()
            except BaseException:
                pass
        return _summary(
            status="error",
            model=model,
            official=True,
            error_code=_error_code_from_exception(exc),
        )

    if not isinstance(report, dict):
        return _summary(status="error", model=model, official=True, error_code="processing_error")

    report_error = _error_code_from_report(report)
    network_summary = _safe_network_summary(report)
    evidence_summary = _safe_evidence_summary(report)
    if report_error is not None:
        # A failed or incomplete transport must never be promoted to an isolation verdict.
        return _summary(
            status="error",
            model=model,
            official=report.get("official") is True,
            error_code=report_error,
            network_summary=network_summary,
            evidence_summary=evidence_summary,
        )

    verdict = report.get("overall_verdict")
    if verdict not in VERDICT_TITLES:
        return _summary(
            status="evidence_insufficient",
            model=model,
            official=report.get("official") is True,
            error_code="evidence_insufficient",
            network_summary=network_summary,
            evidence_summary=evidence_summary,
        )

    return _summary(
        status="complete",
        model=model,
        official=report.get("official") is True,
        error_code=None,
        overall_verdict=verdict,
        title_cn=verdict,
        network_summary=network_summary,
        evidence_summary=evidence_summary,
    )


def main() -> int:
    try:
        request = _read_request()
        model = request.get("model") if isinstance(request, dict) else None
        sys.stdout.write(json.dumps(
            _event("probe.started", model=model, status="running"),
            ensure_ascii=False,
            separators=(",", ":"),
            allow_nan=False,
        ) + "\n")
        sys.stdout.flush()
        response = run_worker(request)
    except BaseException as exc:
        response = _summary(status="error", model=None, official=False, error_code=_error_code_from_exception(exc))
    # Only allow-listed lifecycle and final summary events cross the process boundary.
    sys.stdout.write(json.dumps(
        _event("probe.completed", model=response.get("claimed_model"), status=response.get("status", "error"), summary=response),
        ensure_ascii=False,
        separators=(",", ":"),
        allow_nan=False,
    ) + "\n")
    sys.stdout.flush()
    return 0 if response.get("status") == "complete" else 1


if __name__ == "__main__":
    raise SystemExit(main())
