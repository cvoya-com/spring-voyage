"""Structured specialist replies (#3088).

Every pipeline stage — and the assembly / revise / publish steps — requires the
specialist to answer with a single JSON object so the deterministic engine can
tell a clean hand-off from a deviation, instead of folding whatever prose comes
back as the artifact:

    {"status": "ok", "artifact": "<the complete updated piece>"}
    {"status": "blocked", "reason": "<what stopped you and what would unblock it>"}

The engine parses every correlated reply in
:meth:`orchestrator.coordinator.Coordinator._handle_correlated_reply`:

* ``ok`` → fold the artifact and advance the graph (the golden path);
* ``blocked`` → leave the golden path: funnel to the control-plane LLM;
* unparseable → re-delegate with a bounded retry count, then funnel to the LLM.

This module is pure (stdlib only) so the schema and the tolerant parser are
unit-testable without LangGraph or the SDK.
"""

from __future__ import annotations

import json
import re
from dataclasses import dataclass

STATUS_OK = "ok"
STATUS_BLOCKED = "blocked"
_VALID_STATUSES = frozenset({STATUS_OK, STATUS_BLOCKED})

# A fenced ```json block, if the specialist wrapped the object in one.
_FENCE = re.compile(r"```(?:json)?\s*(\{.*?\})\s*```", re.DOTALL)

# Appended to every brief whose reply the engine parses structurally. Kept here
# (next to the parser) so the contract the writers are told and the contract the
# engine enforces can never drift apart.
STRUCTURED_REPLY_CONTRACT = (
    "\n\n---\n"
    "Reply with a SINGLE JSON object and nothing else — no prose before or after:\n"
    '{"status": "ok", "artifact": "<the complete updated piece, as a JSON string>"}\n'
    "if you finished the work, or\n"
    '{"status": "blocked", "reason": "<what stopped you and what would unblock it>"}\n'
    "if you cannot proceed (the piece you needed is missing, the request is "
    "outside your remit, or anything else prevents you). The artifact must be the "
    "whole piece, not a description of it."
)


@dataclass
class SpecialistReply:
    """A parsed, schema-valid structured reply."""

    status: str
    artifact: str = ""
    reason: str = ""

    @property
    def ok(self) -> bool:
        return self.status == STATUS_OK


def body_from_payload(payload: object, fallback: str) -> str:
    """Extract a message's body from its (possibly structured) A2A payload.

    The platform stores a prose reply as ``{"content": "<prose>"}`` but a
    JSON-bodied reply parsed into the payload object itself — e.g. a specialist's
    ``{"status","artifact"}`` envelope is stored as that object, not wrapped in
    ``{"content": …}``. Return the inner ``content``/``text`` when present, else
    the payload re-serialised as JSON (so the structured-reply parser can read
    it), else *fallback* (the rendered prose). (#3088)
    """
    if isinstance(payload, str) and payload.strip():
        return payload
    if isinstance(payload, dict):
        for key in ("content", "text"):
            value = payload.get(key)
            if isinstance(value, str) and value.strip():
                return value
        if payload:
            return json.dumps(payload)
    return fallback


def parse_specialist_reply(text: str | None) -> SpecialistReply | None:
    """Parse a specialist's structured JSON reply.

    Tolerant of a fenced ```json block or a bare object embedded in prose. Returns
    ``None`` — the signal for the engine to retry — when no schema-valid object is
    found, when ``status`` is missing/unknown, or when an ``ok`` reply carries no
    artifact (so a malformed "success" cannot silently advance an empty piece).
    """
    obj = _extract_object(text or "")
    if obj is None:
        return None
    status = obj.get("status")
    if not isinstance(status, str) or status.lower() not in _VALID_STATUSES:
        return None
    status = status.lower()
    artifact = obj.get("artifact")
    reason = obj.get("reason")
    artifact = artifact if isinstance(artifact, str) else ""
    reason = reason if isinstance(reason, str) else ""
    if status == STATUS_OK and not artifact.strip():
        return None
    return SpecialistReply(status=status, artifact=artifact, reason=reason.strip())


def _extract_object(text: str) -> dict | None:
    """Find the reply's JSON object: the whole text, a fenced block, or the first
    balanced ``{...}`` run embedded in prose."""
    obj = _try_load(text.strip())
    if obj is not None:
        return obj
    fenced = _FENCE.search(text)
    if fenced:
        obj = _try_load(fenced.group(1))
        if obj is not None:
            return obj
    start = text.find("{")
    while start != -1:
        candidate = _balanced(text, start)
        if candidate is not None:
            obj = _try_load(candidate)
            if obj is not None:
                return obj
        start = text.find("{", start + 1)
    return None


def _try_load(candidate: str) -> dict | None:
    try:
        obj = json.loads(candidate)
    except (ValueError, TypeError):
        return None
    return obj if isinstance(obj, dict) else None


def _balanced(text: str, start: int) -> str | None:
    """Return the substring from *start* through its matching ``}``, respecting
    strings/escapes, or ``None`` if it never closes."""
    depth = 0
    in_str = False
    escaped = False
    for i in range(start, len(text)):
        ch = text[i]
        if in_str:
            if escaped:
                escaped = False
            elif ch == "\\":
                escaped = True
            elif ch == '"':
                in_str = False
        elif ch == '"':
            in_str = True
        elif ch == "{":
            depth += 1
        elif ch == "}":
            depth -= 1
            if depth == 0:
                return text[start : i + 1]
    return None
