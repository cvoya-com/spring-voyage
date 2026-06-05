"""
Core types for the Spring Voyage Agent SDK.

Implements the message and response shapes specified in
docs/specs/agent-runtime-boundary.md §1.2.
"""

from __future__ import annotations

import enum
import json
import re
from dataclasses import dataclass, field
from typing import Any


class ShutdownReason(str, enum.Enum):
    """Reason the platform is terminating the container.

    Spec: docs/specs/agent-runtime-boundary.md §1.3.
    """

    requested = "requested"
    """An operator or upstream tenant action requested termination."""

    idle_timeout = "idle_timeout"
    """The platform's idle-eviction policy fired."""

    policy = "policy"
    """A platform-level policy terminated the container."""

    error = "error"
    """The platform detected a fatal condition."""

    platform_restart = "platform_restart"
    """The platform itself is restarting and draining tenant containers."""

    unknown = "unknown"
    """None of the above; default when SIGTERM arrives without a known cause."""


@dataclass
class Sender:
    """The originating participant of an inbound message.

    Spec: docs/specs/agent-runtime-boundary.md §1.2.1.
    """

    kind: str
    """Participant kind: "human" | "agent" | "unit" | "system"."""

    id: str
    """Stable participant identifier."""

    display_name: str | None = None
    """Optional human-readable name."""


@dataclass
class ContextHint:
    """Optional UX-hint metadata carried with a message.

    Spec: docs/specs/agent-runtime-boundary.md §1.2.5.

    The platform does not branch on this field; the SDK passes it through
    verbatim. Unknown ``kind`` values MUST be treated as opaque strings.
    """

    kind: str
    """UX vocabulary: "task_update" | "reminder" | "observation" | "spontaneous" | <unknown>."""

    task: str | None = None
    """Optional task reference, e.g. "#flaky-test-fix"."""

    originating_message: str | None = None
    """Optional message_id that triggered this message."""

    extra: dict[str, Any] = field(default_factory=dict)
    """Unknown fields carried through verbatim."""


_ENVELOPE_JSON_BLOCK = re.compile(r"```json\s*\n(.*?)\n```", re.DOTALL)


@dataclass
class Envelope:
    """The platform's structured inbound envelope (ADR-0060 / ADR-0066 §3).

    The platform renders this envelope into the inbound message text as a
    fenced ``json`` block (``InboundEnvelopeBuilder``). For a deterministic
    runtime — e.g. an orchestration engine that routes on *who* sent *what*
    on *which* conversation rather than reading prose — the SDK parses that
    block and exposes the fields as data on :attr:`Message.envelope`.

    A turn that batched several inbound messages (#3056) renders several
    blocks; :meth:`parse_latest` returns the most recent (last) one, which is
    the message the runtime is being asked to act on.
    """

    from_address: str
    """Sender address (``scheme:guid``). Maps the envelope's ``from`` field."""

    to: list[str] = field(default_factory=list)
    """Recipients the sender targeted (receiver included, sender excluded)."""

    participants: list[str] = field(default_factory=list)
    """Full routable roster (ADR-0064) — the ``respond_to`` delivery set."""

    message_id: str = ""
    """The inbound message's id — the value to pass to ``sv.messaging.respond_to``."""

    timestamp: str | None = None
    """RFC 3339 timestamp the platform stamped on the message."""

    from_display_name: str | None = None
    """Resolved sender display name, when the directory had one."""

    payload: Any = None
    """The raw payload object the envelope carried."""

    @classmethod
    def _from_dict(cls, data: dict[str, Any]) -> "Envelope | None":
        if "from" not in data:
            return None
        return cls(
            from_address=str(data.get("from", "")),
            to=[str(x) for x in data.get("to", []) or []],
            participants=[str(x) for x in data.get("participants", []) or []],
            message_id=str(data.get("message_id", "")),
            timestamp=data.get("timestamp"),
            from_display_name=data.get("from_display_name"),
            payload=data.get("payload"),
        )

    @classmethod
    def parse_all(cls, text: str) -> list["Envelope"]:
        """Parse every fenced ``json`` envelope block in *text*, in order."""
        out: list[Envelope] = []
        for match in _ENVELOPE_JSON_BLOCK.finditer(text or ""):
            try:
                data = json.loads(match.group(1))
            except (ValueError, TypeError):
                continue
            if isinstance(data, dict):
                env = cls._from_dict(data)
                if env is not None:
                    out.append(env)
        return out

    @classmethod
    def parse_latest(cls, text: str) -> "Envelope | None":
        """Return the most recent (last) envelope block in *text*, or ``None``."""
        blocks = cls.parse_all(text)
        return blocks[-1] if blocks else None


@dataclass
class Message:
    """Inbound A2A message delivered to :func:`on_message`.

    Spec: docs/specs/agent-runtime-boundary.md §1.2.1.

    The ``payload`` field carries the raw A2A 0.3.x message body faithfully.
    Convenience accessors (``text``) are provided for the common text-only case.
    """

    thread_id: str
    """Platform-assigned thread identifier; stable across the thread's lifetime."""

    message_id: str
    """Platform-assigned message identifier; unique within the thread."""

    sender: Sender
    """The originating participant."""

    payload: dict[str, Any]
    """Raw A2A 0.3.x message body — typically ``{ role, parts: [...] }``."""

    timestamp: str
    """RFC 3339 timestamp of when the platform received the message."""

    pending_count: int = 0
    """Non-binding hint: how many additional messages are queued for this thread."""

    context: ContextHint | None = None
    """Optional UX-hint metadata; present verbatim when the sender supplied it."""

    mcp_token: str | None = None
    """Per-turn MCP session token for *this* message (ADR-0066 §2).

    The platform issues a fresh MCP token every turn and delivers it in the
    inbound A2A ``message/send`` metadata under ``mcpToken``; it is revoked at
    turn end. An always-on runtime (the ``a2a-process`` host) MUST call
    ``sv.*`` tools with this per-message token, not a token cached at
    ``initialize()`` — that one is empty at persistent cold-start and revoked
    after the first turn. ``None`` when the inbound carried no token metadata
    (e.g. a local harness); callers may then fall back to
    ``IAgentContext.mcp_token``.
    """

    envelope: Envelope | None = None
    """The platform's structured inbound envelope (ADR-0066 §3), parsed from
    the rendered message text. Gives a deterministic runtime the sender,
    recipients, routable participants, and ``message_id`` as data instead of
    prose. ``None`` when no envelope block was present."""

    @property
    def text(self) -> str:
        """Convenience accessor: concatenate all text parts in ``payload``."""
        parts = self.payload.get("parts", [])
        texts: list[str] = []
        for part in parts:
            # Support both dict payloads and objects with a .root accessor
            # (the a2a-sdk v0.3+ discriminated-union Part shape).
            if isinstance(part, dict):
                if part.get("kind") == "text" or part.get("type") == "text":
                    t = part.get("text", "")
                    if t:
                        texts.append(t)
            else:
                root = getattr(part, "root", part)
                t = getattr(root, "text", None)
                if t:
                    texts.append(t)
        return "".join(texts)


@dataclass
class Response:
    """A single streaming chunk emitted by :func:`on_message`.

    Agent authors yield :class:`Response` instances from their
    ``on_message`` implementation. The SDK marshals these into A2A
    streaming responses on the wire.

    Exactly one of ``text``, ``data``, or ``error`` should be set
    per chunk. Set ``final=True`` on the last chunk to signal completion.
    If ``error`` is set the stream terminates; the SDK emits an A2A error
    frame and ignores any subsequent chunks.
    """

    text: str | None = None
    """Incremental text fragment for text-streaming responses."""

    data: Any | None = None
    """Structured payload for non-text chunks."""

    error: str | None = None
    """Error message; terminates the stream with an A2A error frame."""

    final: bool = False
    """When True this is the completion sentinel; no further chunks follow."""
