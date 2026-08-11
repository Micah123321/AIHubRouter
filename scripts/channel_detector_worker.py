"""Run one official gpt56_vnext detector session through a safe stdin/stdout protocol.

The worker deliberately keeps the detector's runtime state in a temporary directory.  The
only persistent output is a small, allow-listed JSON summary; request credentials and raw
probe data never cross that boundary.
"""

from __future__ import annotations

import json
import sys
from pathlib import Path
import tempfile
from typing import Any
from urllib.parse import urlsplit


from channel_detector_worker_protocol import (
    MAX_INPUT_BYTES,
    OUTCOME_CODES,
    OUTCOME_STATES,
    REPORT_SCHEMA_VERSION,
    SUPPORTED_MODELS,
    HARD_OUTCOME_CODES,
    _empty_evidence_summary,
    _empty_network_summary,
    _error_code_from_exception,
    _error_code_from_report,
    _safe_evidence_summary,
    _safe_network_summary,
    _summary,
    _validate_report,
)


REFERENCE_ROOT = Path(__file__).resolve().parents[1] / "gpt56_api_detector"


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

    network_summary = _safe_network_summary(report)
    evidence_summary = _safe_evidence_summary(report)
    report_schema_version = report.get("schema_version") if report.get("schema_version") == REPORT_SCHEMA_VERSION else None
    reported_model = report.get("claimed_model") if report.get("claimed_model") in SUPPORTED_MODELS else None
    reported_outcome = report.get("outcome_code") if report.get("outcome_code") in OUTCOME_CODES else None
    report_error = _error_code_from_report(report)
    if report_error is not None:
        # A failed or incomplete transport must never be promoted to an isolation verdict.
        return _summary(
            status="error",
            model=model,
            official=report.get("official") is True,
            error_code=report_error,
            report_schema_version=report_schema_version,
            outcome_code=reported_outcome,
            claimed_model=reported_model,
            network_summary=network_summary,
            evidence_summary=evidence_summary,
        )

    validation_error = _validate_report(report, model, network_summary)
    if validation_error is not None:
        status = "evidence_insufficient" if validation_error == "evidence_insufficient" else "error"
        return _summary(
            status=status,
            model=model,
            official=report.get("official") is True,
            error_code=validation_error,
            report_schema_version=report_schema_version,
            outcome_code=reported_outcome,
            juice_state=report.get("juice_verdict_state"),
            fingerprint_state=report.get("fingerprint_verdict_state"),
            fingerprint_model=report.get("fingerprint_model"),
            claimed_model=reported_model,
            network_summary=network_summary,
            evidence_summary=evidence_summary,
        )

    outcome_code = report["outcome_code"]
    return _summary(
        status="complete",
        model=model,
        official=report.get("official") is True,
        error_code=None,
        overall_verdict=report["overall_verdict"],
        title_cn=report["title_cn"],
        report_schema_version=REPORT_SCHEMA_VERSION,
        outcome_code=outcome_code,
        juice_state=report["juice_verdict_state"],
        fingerprint_state=report["fingerprint_verdict_state"],
        fingerprint_model=report.get("fingerprint_model"),
        claimed_model=report["claimed_model"],
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
