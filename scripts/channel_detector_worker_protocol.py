"""Stable schema adapter for the 4.1.0 detector report boundary."""

from __future__ import annotations

from collections import Counter
from typing import Any


SUPPORTED_MODELS = frozenset({"gpt-5.6-sol", "gpt-5.6-terra", "gpt-5.6-luna"})
REPORT_SCHEMA_VERSION = 3
OUTCOME_CODES = frozenset(
    {
        "juice_pass_fingerprint_strong",
        "juice_pass_fingerprint_unclear",
        "juice_mismatch_fingerprint_strong",
        "juice_mismatch_fingerprint_unclear",
        "juice_insufficient_fingerprint_strong",
        "juice_insufficient_fingerprint_unclear",
        "possible_non_gpt",
    }
)
HARD_OUTCOME_CODES = frozenset(
    {
        "possible_non_gpt",
        "juice_mismatch_fingerprint_strong",
        "juice_mismatch_fingerprint_unclear",
    }
)
JUICE_STATES = frozenset({"pass", "mismatch", "insufficient", "possible_non_gpt"})
FINGERPRINT_STATES = frozenset({"strong_match", "unclear"})
FINGERPRINT_REASON_CODES = frozenset(
    {
        "no_exact_runtime_contract",
        "runtime_reference_only",
        "runtime_contract_mismatch",
        "baseline_cells_missing",
        "candidate_samples_incomplete",
        "no_weighted_fingerprint_family",
        "no_model_reached_strong_match_threshold",
        "multiple_models_reached_threshold",
        "custom_probe_reference_only",
        "builtin_fingerprint_not_enabled",
    }
)
OUTCOME_STATES: dict[str, tuple[str, str | None]] = {
    "juice_pass_fingerprint_strong": ("pass", "strong_match"),
    "juice_pass_fingerprint_unclear": ("pass", "unclear"),
    "juice_mismatch_fingerprint_strong": ("mismatch", "strong_match"),
    "juice_mismatch_fingerprint_unclear": ("mismatch", "unclear"),
    "juice_insufficient_fingerprint_strong": ("insufficient", "strong_match"),
    "juice_insufficient_fingerprint_unclear": ("insufficient", "unclear"),
    "possible_non_gpt": ("possible_non_gpt", "unclear"),
}
MAX_INPUT_BYTES = 1_048_576
MAX_SCALAR_LENGTH = 4096
NETWORK_INTEGER_FIELDS = (
    "logical_tasks",
    "logical_completed",
    "successful",
    "final_errors",
    "cancelled",
    "http_attempts",
    "retries",
    "in_flight",
)
EVIDENCE_INTEGER_FIELDS = {
    "juice_valid_completed",
    "juice_current_success",
    "juice_mixed",
    "juice_network_errors",
    "output_requests",
    "output_exact",
    "coverage_requests",
}
EVIDENCE_BOOLEAN_FIELDS = {
    "verdict_available",
    "hard_verdict",
    "coverage_hard_anomaly",
    "fingerprint_enabled",
    "evidence_insufficient",
}


def _empty_network_summary() -> dict[str, Any]:
    return {**{key: 0 for key in NETWORK_INTEGER_FIELDS}, "error_categories": {}}


def _empty_evidence_summary() -> dict[str, Any]:
    return {
        "report_schema_version": None,
        "outcome_code": None,
        "verdict_available": False,
        "hard_verdict": False,
        "juice_state": "unknown",
        "fingerprint_state": "unknown",
        "fingerprint_model": None,
        "juice_valid_completed": 0,
        "juice_current_success": 0,
        "juice_mixed": 0,
        "juice_network_errors": 0,
        "output_requests": 0,
        "output_exact": 0,
        "coverage_requests": 0,
        "coverage_hard_anomaly": False,
        "fingerprint_enabled": False,
        "fingerprint_formal_eligible": None,
        "evidence_insufficient": True,
    }


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
    for key in NETWORK_INTEGER_FIELDS:
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
    juice = report.get("juice_summary") if isinstance(report.get("juice_summary"), dict) else {}
    output = report.get("output_integrity_summary") if isinstance(report.get("output_integrity_summary"), dict) else {}
    coverage = report.get("coverage_summary") if isinstance(report.get("coverage_summary"), dict) else {}
    fingerprint = report.get("fingerprint_summary") if isinstance(report.get("fingerprint_summary"), dict) else {}
    reasons = fingerprint.get("fingerprint_unclear_reasons")
    reasons = [item for item in reasons if item in FINGERPRINT_REASON_CODES] if isinstance(reasons, list) else []

    outcome_code = report.get("outcome_code") if report.get("outcome_code") in OUTCOME_CODES else None
    juice_state = report.get("juice_verdict_state") if report.get("juice_verdict_state") in JUICE_STATES else "unknown"
    fingerprint_state = (
        report.get("fingerprint_verdict_state")
        if report.get("fingerprint_verdict_state") in FINGERPRINT_STATES
        else "unknown"
    )
    fingerprint_model = report.get("fingerprint_model")
    fingerprint_model = fingerprint_model if fingerprint_model in SUPPORTED_MODELS else None
    formal_eligible = fingerprint.get("fingerprint_official_eligible")
    formal_eligible = formal_eligible if isinstance(formal_eligible, bool) else None

    return {
        "report_schema_version": report.get("schema_version") if report.get("schema_version") == REPORT_SCHEMA_VERSION else None,
        "outcome_code": outcome_code,
        "verdict_available": report.get("verdict_available") is True and outcome_code is not None,
        "hard_verdict": outcome_code in HARD_OUTCOME_CODES,
        "juice_state": juice_state,
        "fingerprint_state": fingerprint_state,
        "fingerprint_model": fingerprint_model,
        "juice_valid_completed": _integer(juice.get("valid_completed")),
        "juice_current_success": _integer(juice.get("current_success")),
        "juice_mixed": _integer(juice.get("mixed")),
        "juice_network_errors": _integer(juice.get("network_errors")),
        "output_requests": _integer(output.get("requests")),
        "output_exact": _integer(output.get("exact")),
        "coverage_requests": _integer(coverage.get("requests")),
        "coverage_hard_anomaly": coverage.get("hard_anomaly") is True,
        "fingerprint_enabled": "builtin_fingerprint_not_enabled" not in reasons,
        "fingerprint_formal_eligible": formal_eligible,
        "evidence_insufficient": outcome_code is None or outcome_code.startswith("juice_insufficient_"),
    }


def _copy_network_summary(value: Any) -> dict[str, Any]:
    if not isinstance(value, dict):
        return _empty_network_summary()
    summary = _empty_network_summary()
    for key in NETWORK_INTEGER_FIELDS:
        summary[key] = _integer(value.get(key))
    categories = value.get("error_categories")
    if isinstance(categories, dict):
        summary["error_categories"] = {
            str(key): _integer(count)
            for key, count in categories.items()
            if isinstance(key, str) and _integer(count) > 0 and len(key) <= 64
        }
    return summary


def _copy_evidence_summary(value: Any) -> dict[str, Any]:
    summary = _empty_evidence_summary()
    if not isinstance(value, dict):
        return summary
    for key in EVIDENCE_INTEGER_FIELDS:
        summary[key] = _integer(value.get(key))
    for key in EVIDENCE_BOOLEAN_FIELDS:
        summary[key] = value.get(key) is True
    for key in {"report_schema_version", "fingerprint_formal_eligible"}:
        candidate = value.get(key)
        summary[key] = candidate if isinstance(candidate, (bool, int)) else None
    for key in {"outcome_code", "juice_state", "fingerprint_state", "fingerprint_model"}:
        candidate = value.get(key)
        summary[key] = candidate if isinstance(candidate, str) and len(candidate) <= 128 else None
    return summary


def _summary(
    *,
    status: str,
    model: str | None,
    official: bool,
    error_code: str | None,
    overall_verdict: str | None = None,
    title_cn: str = "未形成正式结论",
    report_schema_version: int | None = None,
    outcome_code: str | None = None,
    juice_state: str | None = None,
    fingerprint_state: str | None = None,
    fingerprint_model: str | None = None,
    claimed_model: str | None = None,
    network_summary: dict[str, Any] | None = None,
    evidence_summary: dict[str, Any] | None = None,
) -> dict[str, Any]:
    """Build the fixed, allow-listed worker response shape."""

    return {
        "status": status,
        "overall_verdict": overall_verdict if isinstance(overall_verdict, str) else None,
        "title_cn": title_cn if overall_verdict is not None else "未形成正式结论",
        "official": bool(official),
        "claimed_model": claimed_model if claimed_model in SUPPORTED_MODELS else model if model in SUPPORTED_MODELS else None,
        "report_schema_version": report_schema_version if report_schema_version == REPORT_SCHEMA_VERSION else None,
        "outcome_code": outcome_code if outcome_code in OUTCOME_CODES else None,
        "juice_state": juice_state if juice_state in JUICE_STATES else "unknown",
        "fingerprint_state": fingerprint_state if fingerprint_state in FINGERPRINT_STATES else "unknown",
        "fingerprint_model": fingerprint_model if fingerprint_model in SUPPORTED_MODELS else None,
        "network_summary": _copy_network_summary(network_summary),
        "evidence_summary": _copy_evidence_summary(evidence_summary),
        "error_code": error_code,
    }


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


def _validate_report(report: dict[str, Any], requested_model: str, network_summary: dict[str, Any]) -> str | None:
    """Validate the 4.1.0 report contract before exposing a conclusion."""

    if report.get("schema_version") != REPORT_SCHEMA_VERSION:
        return "unsupported_schema"
    if report.get("operational_status") != "complete" or report.get("verdict_available") is not True:
        return "processing_error"
    if report.get("preset") != "low" or report.get("mode") != "single":
        return "evidence_insufficient"
    if (
        report.get("official") is not True
        or report.get("official_grade") is not True
        or report.get("trust_scope") != "official_preset"
        or report.get("custom_preset") is True
    ):
        return "evidence_insufficient"
    if report.get("claimed_model") != requested_model:
        return "evidence_insufficient"

    outcome_code = report.get("outcome_code")
    if outcome_code not in OUTCOME_CODES:
        return "evidence_insufficient"
    expected_juice, expected_fingerprint = OUTCOME_STATES[outcome_code]
    if report.get("juice_verdict_state") != expected_juice or report.get("fingerprint_verdict_state") != expected_fingerprint:
        return "evidence_insufficient"

    fingerprint = report.get("fingerprint_summary")
    if not isinstance(fingerprint, dict) or fingerprint.get("schema_version") != REPORT_SCHEMA_VERSION:
        return "evidence_insufficient"
    if fingerprint.get("fingerprint_status") != expected_fingerprint:
        return "evidence_insufficient"
    reported_fingerprint_model = report.get("fingerprint_model")
    if reported_fingerprint_model != fingerprint.get("fingerprint_model"):
        return "evidence_insufficient"
    if expected_fingerprint == "strong_match":
        if reported_fingerprint_model not in SUPPORTED_MODELS or fingerprint.get("fingerprint_official_eligible") is not True:
            return "evidence_insufficient"
    elif reported_fingerprint_model is not None:
        return "evidence_insufficient"

    for key in ("overall_verdict", "title_cn"):
        value = report.get(key)
        if not isinstance(value, str) or not value or len(value) > MAX_SCALAR_LENGTH:
            return "evidence_insufficient"
    if (
        network_summary["logical_tasks"] <= 0
        or network_summary["logical_completed"] != network_summary["logical_tasks"]
        or network_summary["successful"] != network_summary["logical_tasks"]
        or network_summary["final_errors"] != 0
        or network_summary["cancelled"] != 0
        or network_summary["in_flight"] != 0
        or network_summary["error_categories"]
    ):
        return "processing_error"
    if report.get("retention_complete") is False or report.get("run_stopped") is True:
        return "processing_error"
    return None


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
