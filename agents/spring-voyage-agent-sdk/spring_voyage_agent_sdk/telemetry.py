"""
OTLP/HTTP+JSON telemetry emitter for the SDK.

Implements the producer side of the wire contract documented at
``src/Cvoya.Spring.Host.Api/Endpoints/Otlp/OtlpEventMapper.cs`` (issue
#2492). The platform ingest expects:

  * Spans named ``sv.tool.call`` and ``sv.llm.turn`` — both get
    promoted to typed activity events on the bus.
  * Span events named ``sv.progress`` carrying a ``message`` attribute
    — promoted to ``RuntimeProgress`` activity events.
  * Resource attributes ``sv.tenant.id``, ``sv.subject.uuid``,
    ``sv.subject.kind`` cross-checked against the bearer-token claims.

The launcher sets the OTLP env vars (``OTEL_EXPORTER_OTLP_ENDPOINT``,
``OTEL_EXPORTER_OTLP_HEADERS``, ``OTEL_RESOURCE_ATTRIBUTES``,
``OTEL_SERVICE_NAME``) — see
``src/Cvoya.Spring.AgentRuntimes/Launchers/LauncherOtelEnvironment.cs``
for the exact contract. This module reads them at construction time
and does not require a separate config call.

Design notes
------------

The OpenTelemetry Python SDK is too heavy a dependency for a small
SDK whose primary job is to wrap A2A. The wire format is well-defined
and stable; we serialise OTLP/HTTP+JSON envelopes ourselves with
``json.dumps``. The full OTel SDK is still available to agent authors
who want richer instrumentation; nothing in this module precludes
running both.

A broken telemetry path MUST NOT break the agent's reply path. Every
emission is wrapped in a try/except and any failure logs at debug
level and returns — the caller's flow continues.
"""

from __future__ import annotations

import json
import logging
import os
import secrets
import threading
import time
import urllib.error
import urllib.request
from typing import Any

logger = logging.getLogger("spring-voyage-agent-sdk.telemetry")

_OTLP_ENDPOINT_ENV = "OTEL_EXPORTER_OTLP_ENDPOINT"
_OTLP_HEADERS_ENV = "OTEL_EXPORTER_OTLP_HEADERS"
_OTLP_RESOURCE_ATTRS_ENV = "OTEL_RESOURCE_ATTRIBUTES"
_OTLP_SERVICE_NAME_ENV = "OTEL_SERVICE_NAME"

_DEFAULT_TIMEOUT_SECONDS = 5.0


def _now_unix_nanos() -> int:
    """Return the current time as Unix nanoseconds, the OTLP wire unit."""
    return time.time_ns()


def _random_hex(byte_count: int) -> str:
    """Return ``byte_count`` random bytes as a lowercase hex string.

    OTLP span / trace ids are 8 / 16 byte values encoded as hex on the
    JSON wire (per the otlp-http-json spec).
    """
    return secrets.token_hex(byte_count)


def _kv_pair(key: str, value: Any) -> dict[str, Any]:
    """Build an OTLP KeyValue entry for the JSON wire shape."""
    if isinstance(value, bool):
        return {"key": key, "value": {"boolValue": value}}
    if isinstance(value, int):
        return {"key": key, "value": {"intValue": str(value)}}
    if isinstance(value, float):
        return {"key": key, "value": {"doubleValue": value}}
    if isinstance(value, (list, tuple)):
        return {
            "key": key,
            "value": {"arrayValue": {"values": [_kv_pair("_", v)["value"] for v in value]}},
        }
    return {"key": key, "value": {"stringValue": str(value)}}


def _parse_resource_attributes(raw: str | None) -> dict[str, str]:
    """Parse the comma-separated ``k=v,k=v`` OTel env value.

    Whitespace and empty entries are tolerated; values containing
    commas are not supported (they're already disallowed by the OTel
    env-var spec).
    """
    out: dict[str, str] = {}
    if not raw:
        return out
    for entry in raw.split(","):
        entry = entry.strip()
        if not entry or "=" not in entry:
            continue
        k, _, v = entry.partition("=")
        if k and v:
            out[k.strip()] = v.strip()
    return out


def _parse_headers(raw: str | None) -> dict[str, str]:
    """Parse the comma-separated ``k=v,k=v`` OTel headers env value."""
    return _parse_resource_attributes(raw)


class TelemetryEmitter:
    """OTLP/HTTP+JSON producer wired up from launcher-injected env vars.

    Constructed once per agent process. Cheap if no endpoint is
    configured — every emission method becomes a no-op without touching
    the network.
    """

    def __init__(
        self,
        *,
        endpoint: str | None = None,
        headers: dict[str, str] | None = None,
        resource_attributes: dict[str, str] | None = None,
        service_name: str | None = None,
        timeout_seconds: float = _DEFAULT_TIMEOUT_SECONDS,
    ) -> None:
        # Env wins over caller-supplied values are NOT applied here —
        # the call-site picks one shape. Tests construct directly
        # without env; the SDK runtime calls ``from_environment``.
        self._endpoint = (endpoint or "").rstrip("/")
        self._headers = dict(headers or {})
        self._resource_attributes = dict(resource_attributes or {})
        if service_name:
            self._resource_attributes.setdefault("service.name", service_name)
        self._timeout = timeout_seconds
        self._enabled = bool(self._endpoint)
        self._post_lock = threading.Lock()

    @classmethod
    def from_environment(cls) -> "TelemetryEmitter":
        """Construct from the launcher-injected OTel env vars."""
        endpoint = os.environ.get(_OTLP_ENDPOINT_ENV, "").strip()
        headers = _parse_headers(os.environ.get(_OTLP_HEADERS_ENV))
        resource_attrs = _parse_resource_attributes(os.environ.get(_OTLP_RESOURCE_ATTRS_ENV))
        service_name = os.environ.get(_OTLP_SERVICE_NAME_ENV)
        return cls(
            endpoint=endpoint,
            headers=headers,
            resource_attributes=resource_attrs,
            service_name=service_name,
        )

    @property
    def enabled(self) -> bool:
        """Whether OTLP emission is wired (``False`` when no endpoint env was set)."""
        return self._enabled

    @property
    def resource_attributes(self) -> dict[str, str]:
        """Read-only view of resource attributes stamped on every payload."""
        return dict(self._resource_attributes)

    @property
    def subject_uuid(self) -> str:
        """Subject uuid from resource attributes — drives the rate limiter key."""
        return self._resource_attributes.get("sv.subject.uuid", "")

    def emit_span(
        self,
        *,
        name: str,
        trace_id: str,
        span_id: str,
        parent_span_id: str | None,
        start_unix_nanos: int,
        end_unix_nanos: int,
        attributes: dict[str, Any] | None = None,
        events: list[dict[str, Any]] | None = None,
        status_code: int | None = None,
        status_message: str | None = None,
    ) -> bool:
        """Ship one OTLP span to ``/v1/traces``. Returns ``True`` on success."""
        if not self._enabled:
            return False

        span_payload: dict[str, Any] = {
            "name": name,
            "traceId": trace_id,
            "spanId": span_id,
            "kind": 1,
            "startTimeUnixNano": str(start_unix_nanos),
            "endTimeUnixNano": str(end_unix_nanos),
            "attributes": [_kv_pair(k, v) for k, v in (attributes or {}).items()],
            "events": events or [],
        }
        if parent_span_id:
            span_payload["parentSpanId"] = parent_span_id
        if status_code is not None:
            status: dict[str, Any] = {"code": status_code}
            if status_message:
                status["message"] = status_message
            span_payload["status"] = status

        envelope = {
            "resourceSpans": [
                {
                    "resource": {
                        "attributes": [_kv_pair(k, v) for k, v in self._resource_attributes.items()],
                    },
                    "scopeSpans": [
                        {
                            "scope": {"name": "spring-voyage-agent-sdk", "version": "0.1.0"},
                            "spans": [span_payload],
                        }
                    ],
                }
            ]
        }
        return self._post("/v1/traces", envelope)

    def emit_log(
        self,
        *,
        severity: str,
        body: str,
        attributes: dict[str, Any] | None = None,
        trace_id: str | None = None,
        span_id: str | None = None,
    ) -> bool:
        """Ship one OTLP log record to ``/v1/logs``."""
        if not self._enabled:
            return False

        severity_number = {
            "TRACE": 1,
            "DEBUG": 5,
            "INFO": 9,
            "WARN": 13,
            "WARNING": 13,
            "ERROR": 17,
            "FATAL": 21,
        }.get(severity.upper(), 9)

        record: dict[str, Any] = {
            "timeUnixNano": str(_now_unix_nanos()),
            "severityText": severity.upper(),
            "severityNumber": severity_number,
            "body": {"stringValue": body},
            "attributes": [_kv_pair(k, v) for k, v in (attributes or {}).items()],
        }
        if trace_id:
            record["traceId"] = trace_id
        if span_id:
            record["spanId"] = span_id

        envelope = {
            "resourceLogs": [
                {
                    "resource": {
                        "attributes": [_kv_pair(k, v) for k, v in self._resource_attributes.items()],
                    },
                    "scopeLogs": [
                        {
                            "scope": {"name": "spring-voyage-agent-sdk", "version": "0.1.0"},
                            "logRecords": [record],
                        }
                    ],
                }
            ]
        }
        return self._post("/v1/logs", envelope)

    @staticmethod
    def progress_event(
        message: str,
        *,
        unix_nanos: int | None = None,
        kind: str | None = None,
        attrs: dict[str, Any] | None = None,
    ) -> dict[str, Any]:
        """Build a span-event entry to attach to a span.

        Promoted to a ``RuntimeProgress`` activity event by the ingest
        when ``name == "sv.progress"`` and the ``message`` attribute is
        present (see ``OtlpEventMapper.MapProgressEvent``).
        """
        event_attrs: dict[str, Any] = {"message": message}
        if kind:
            event_attrs["kind"] = kind
        if attrs:
            for k, v in attrs.items():
                if k in event_attrs:
                    continue
                event_attrs[k] = v
        return {
            "name": "sv.progress",
            "timeUnixNano": str(unix_nanos if unix_nanos is not None else _now_unix_nanos()),
            "attributes": [_kv_pair(k, v) for k, v in event_attrs.items()],
        }

    def _post(self, path: str, envelope: dict[str, Any]) -> bool:
        """POST one envelope; best-effort, swallows transport errors."""
        if not self._enabled:
            return False

        url = f"{self._endpoint}{path}"
        try:
            body = json.dumps(envelope).encode("utf-8")
        except (TypeError, ValueError) as exc:
            logger.debug("Failed to serialize OTLP payload for %s: %s", path, exc)
            return False

        request = urllib.request.Request(
            url,
            data=body,
            method="POST",
            headers={"Content-Type": "application/json", **self._headers},
        )

        # urllib is not thread-safe at the global-handler level; serialise
        # writes here. Per-event volume is bounded by the rate limiter
        # already, so this isn't a hot path.
        with self._post_lock:
            try:
                with urllib.request.urlopen(request, timeout=self._timeout) as resp:
                    return 200 <= resp.status < 300
            except urllib.error.URLError as exc:
                logger.debug("OTLP POST %s failed (URLError): %s", url, exc)
            except TimeoutError as exc:
                logger.debug("OTLP POST %s timed out: %s", url, exc)
            except Exception as exc:  # noqa: BLE001 — best-effort emission
                logger.debug("OTLP POST %s raised %s: %s", url, type(exc).__name__, exc)
            return False


def new_trace_id() -> str:
    """Generate a fresh OTLP-compatible trace id (16 bytes / 32 hex chars)."""
    return _random_hex(16)


def new_span_id() -> str:
    """Generate a fresh OTLP-compatible span id (8 bytes / 16 hex chars)."""
    return _random_hex(8)
