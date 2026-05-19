"""
OTLP/HTTP+protobuf encoder for the SDK's emit path (issue #2501).

Uses the official ``opentelemetry-proto`` generated types so the wire
format is bit-for-bit identical to what the OpenTelemetry SDK emits
when configured for ``http/protobuf``. The SDK never imports
``opentelemetry-sdk`` itself — that's a much larger dependency tree —
but the proto messages are tiny and stable.

The envelope shapes accepted here mirror what the existing JSON path
builds in ``telemetry.TelemetryEmitter`` so the call-sites stay
unchanged between protocols.
"""

from __future__ import annotations

from typing import Any

from opentelemetry.proto.collector.logs.v1 import logs_service_pb2
from opentelemetry.proto.collector.trace.v1 import trace_service_pb2
from opentelemetry.proto.common.v1 import common_pb2
from opentelemetry.proto.logs.v1 import logs_pb2
from opentelemetry.proto.resource.v1 import resource_pb2
from opentelemetry.proto.trace.v1 import trace_pb2


def encode_traces_envelope(envelope: dict[str, Any]) -> bytes:
    """Encode an ``ExportTraceServiceRequest`` from a JSON-pathway dict."""
    request = trace_service_pb2.ExportTraceServiceRequest()
    for resource_spans in envelope.get("resourceSpans", []):
        out = request.resource_spans.add()
        resource = resource_spans.get("resource")
        if resource is not None:
            _fill_resource(out.resource, resource)
        for scope_spans in resource_spans.get("scopeSpans", []):
            scope_out = out.scope_spans.add()
            for span in scope_spans.get("spans", []):
                _fill_span(scope_out.spans.add(), span)
    return request.SerializeToString()


def encode_logs_envelope(envelope: dict[str, Any]) -> bytes:
    """Encode an ``ExportLogsServiceRequest`` from a JSON-pathway dict."""
    request = logs_service_pb2.ExportLogsServiceRequest()
    for resource_logs in envelope.get("resourceLogs", []):
        out = request.resource_logs.add()
        resource = resource_logs.get("resource")
        if resource is not None:
            _fill_resource(out.resource, resource)
        for scope_logs in resource_logs.get("scopeLogs", []):
            scope_out = out.scope_logs.add()
            for record in scope_logs.get("logRecords", []):
                _fill_log_record(scope_out.log_records.add(), record)
    return request.SerializeToString()


# ---------------------------------------------------------------------------
# Span / log / kv fillers
# ---------------------------------------------------------------------------


def _fill_resource(out: resource_pb2.Resource, resource: dict[str, Any]) -> None:
    for attr in resource.get("attributes", []):
        _fill_kv(out.attributes.add(), attr)


def _fill_span(out: trace_pb2.Span, span: dict[str, Any]) -> None:
    if trace_id := span.get("traceId"):
        out.trace_id = bytes.fromhex(trace_id)
    if span_id := span.get("spanId"):
        out.span_id = bytes.fromhex(span_id)
    if parent := span.get("parentSpanId"):
        out.parent_span_id = bytes.fromhex(parent)
    if name := span.get("name"):
        out.name = name
    if (kind := span.get("kind")) is not None:
        out.kind = int(kind)
    if (start := span.get("startTimeUnixNano")) is not None:
        out.start_time_unix_nano = int(start)
    if (end := span.get("endTimeUnixNano")) is not None:
        out.end_time_unix_nano = int(end)
    for attr in span.get("attributes", []):
        _fill_kv(out.attributes.add(), attr)
    for event in span.get("events", []):
        ev_out = out.events.add()
        if (ev_name := event.get("name")) is not None:
            ev_out.name = ev_name
        if (ev_time := event.get("timeUnixNano")) is not None:
            ev_out.time_unix_nano = int(ev_time)
        for ev_attr in event.get("attributes", []):
            _fill_kv(ev_out.attributes.add(), ev_attr)
    if (status := span.get("status")) is not None:
        if (msg := status.get("message")) is not None:
            out.status.message = msg
        if (code := status.get("code")) is not None:
            out.status.code = int(code)


def _fill_log_record(out: logs_pb2.LogRecord, record: dict[str, Any]) -> None:
    if (time_nanos := record.get("timeUnixNano")) is not None:
        out.time_unix_nano = int(time_nanos)
    if (sev_num := record.get("severityNumber")) is not None:
        out.severity_number = int(sev_num)
    if (sev_text := record.get("severityText")) is not None:
        out.severity_text = sev_text
    if (body := record.get("body")) is not None:
        _fill_any_value(out.body, body)
    for attr in record.get("attributes", []):
        _fill_kv(out.attributes.add(), attr)
    if trace_id := record.get("traceId"):
        out.trace_id = bytes.fromhex(trace_id)
    if span_id := record.get("spanId"):
        out.span_id = bytes.fromhex(span_id)


def _fill_kv(out: common_pb2.KeyValue, kv: dict[str, Any]) -> None:
    out.key = kv.get("key", "")
    if (value := kv.get("value")) is not None:
        _fill_any_value(out.value, value)


def _fill_any_value(out: common_pb2.AnyValue, value: dict[str, Any]) -> None:
    """Project a JSON-pathway AnyValue dict onto the protobuf message."""
    if "stringValue" in value:
        out.string_value = value["stringValue"]
        return
    if "boolValue" in value:
        out.bool_value = bool(value["boolValue"])
        return
    if "intValue" in value:
        # The JSON pathway encodes ints as strings to dodge JSON's
        # 53-bit precision floor. Protobuf wants an int64.
        try:
            out.int_value = int(value["intValue"])
        except (TypeError, ValueError):
            out.string_value = str(value["intValue"])
        return
    if "doubleValue" in value:
        out.double_value = float(value["doubleValue"])
        return
    if "arrayValue" in value:
        for entry in value["arrayValue"].get("values", []):
            _fill_any_value(out.array_value.values.add(), entry)
        return
    if "kvlistValue" in value:
        for entry in value["kvlistValue"].get("values", []):
            _fill_kv(out.kvlist_value.values.add(), entry)
        return
    # No typed field set — leave the AnyValue empty.
