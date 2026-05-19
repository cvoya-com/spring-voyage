"""Tests for the SDK's OTLP/HTTP+JSON emitter (#2493)."""

from __future__ import annotations

import json
from typing import Any
from unittest.mock import patch

import pytest

from spring_voyage_agent_sdk.telemetry import (
    TelemetryEmitter,
    new_span_id,
    new_trace_id,
)


class _FakeResponse:
    def __init__(self, status: int = 200) -> None:
        self.status = status

    def __enter__(self) -> "_FakeResponse":
        return self

    def __exit__(self, *args: Any) -> None:
        return None


class TestTelemetryEmitter:
    def test_disabled_when_no_endpoint(self):
        emitter = TelemetryEmitter(endpoint=None)
        assert emitter.enabled is False
        # All emit_* methods are no-ops when disabled.
        assert emitter.emit_span(
            name="x",
            trace_id="0" * 32,
            span_id="0" * 16,
            parent_span_id=None,
            start_unix_nanos=0,
            end_unix_nanos=0,
        ) is False
        assert emitter.emit_log(severity="INFO", body="hi") is False

    def test_emit_span_posts_otlp_envelope(self):
        captured: dict[str, Any] = {}

        def _fake_urlopen(request, timeout):  # noqa: ANN001
            captured["url"] = request.full_url
            captured["body"] = json.loads(request.data.decode("utf-8"))
            captured["headers"] = dict(request.headers)
            return _FakeResponse(200)

        with patch("urllib.request.urlopen", side_effect=_fake_urlopen):
            emitter = TelemetryEmitter(
                endpoint="https://api.example.com/otlp",
                headers={"Authorization": "Bearer tok"},
                resource_attributes={
                    "sv.tenant.id": "tenant1",
                    "sv.subject.uuid": "subject1",
                    "sv.subject.kind": "agent",
                },
            )
            ok = emitter.emit_span(
                name="sv.tool.call",
                trace_id=new_trace_id(),
                span_id=new_span_id(),
                parent_span_id=new_span_id(),
                start_unix_nanos=1000,
                end_unix_nanos=2000,
                attributes={"tool.name": "acme.echo"},
            )

        assert ok is True
        assert captured["url"] == "https://api.example.com/otlp/v1/traces"
        assert "Authorization" in captured["headers"]
        # Envelope shape — must mirror what OtlpEventMapper consumes.
        body = captured["body"]
        assert "resourceSpans" in body
        resource = body["resourceSpans"][0]["resource"]
        attrs = {a["key"]: a["value"]["stringValue"] for a in resource["attributes"]}
        assert attrs["sv.tenant.id"] == "tenant1"
        assert attrs["sv.subject.uuid"] == "subject1"
        assert attrs["sv.subject.kind"] == "agent"
        span = body["resourceSpans"][0]["scopeSpans"][0]["spans"][0]
        assert span["name"] == "sv.tool.call"
        tool_attr = next(a for a in span["attributes"] if a["key"] == "tool.name")
        assert tool_attr["value"]["stringValue"] == "acme.echo"

    def test_emit_swallows_transport_errors(self, caplog: pytest.LogCaptureFixture):
        """A broken telemetry path must NOT break the reply path."""
        import urllib.error

        def _fail(_request, timeout):  # noqa: ANN001
            raise urllib.error.URLError("connection refused")

        with patch("urllib.request.urlopen", side_effect=_fail):
            emitter = TelemetryEmitter(endpoint="https://api.example.com/otlp")
            # Must return False, not raise.
            assert emitter.emit_span(
                name="sv.tool.call",
                trace_id="0" * 32,
                span_id="0" * 16,
                parent_span_id=None,
                start_unix_nanos=0,
                end_unix_nanos=0,
            ) is False

    def test_from_environment_parses_headers_and_resource_attrs(
        self, monkeypatch: pytest.MonkeyPatch
    ):
        monkeypatch.setenv("OTEL_EXPORTER_OTLP_ENDPOINT", "https://otlp.example/otlp")
        monkeypatch.setenv("OTEL_EXPORTER_OTLP_HEADERS", "Authorization=Bearer t,X-A=1")
        monkeypatch.setenv(
            "OTEL_RESOURCE_ATTRIBUTES",
            "sv.tenant.id=t1,sv.subject.uuid=s1,sv.subject.kind=agent",
        )
        monkeypatch.setenv("OTEL_SERVICE_NAME", "spring-voyage/agent")

        emitter = TelemetryEmitter.from_environment()
        assert emitter.enabled is True
        assert emitter.subject_uuid == "s1"
        assert emitter.resource_attributes["sv.tenant.id"] == "t1"
        assert emitter.resource_attributes["service.name"] == "spring-voyage/agent"

    def test_progress_event_shape(self):
        event = TelemetryEmitter.progress_event(
            "starting work",
            unix_nanos=1234,
            kind="progress",
            attrs={"foo": "bar"},
        )
        assert event["name"] == "sv.progress"
        attrs = {a["key"]: a["value"] for a in event["attributes"]}
        assert attrs["message"]["stringValue"] == "starting work"
        assert attrs["kind"]["stringValue"] == "progress"
        assert attrs["foo"]["stringValue"] == "bar"
