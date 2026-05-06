"""
Core types for the Spring Voyage Agent SDK.

Implements the message and response shapes specified in
docs/specs/agent-runtime-boundary.md §1.2.
"""

from __future__ import annotations

import enum
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
