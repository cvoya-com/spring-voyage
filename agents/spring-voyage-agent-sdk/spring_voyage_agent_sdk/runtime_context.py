"""
RuntimeContext — telemetry surface exposed to ``on_message`` handlers.

Issue #2493. The runtime constructs one ``RuntimeContext`` per turn and
binds it via a ``contextvars.ContextVar`` so the handler can read it
without an explicit argument. The context exposes:

  * ``report_progress(text, kind=None, attrs=None)`` — span event on
    the active turn span, emitted via the OTLP plane (#2492).
  * ``tool_call(name, args)`` — async context manager wrapping one tool
    invocation as an ``sv.tool.call`` span.
  * ``llm_turn(model, prompt)`` — async context manager wrapping one
    LLM call as an ``sv.llm.turn`` span.

Every emission goes through the SDK's :class:`ProgressRateLimiter` so a
runaway loop cannot flood the ingest. OTel transport failures are
swallowed by the underlying :class:`TelemetryEmitter` — the agent's
reply path must never be blocked by a broken telemetry plane (issue
#2493 "Non-negotiable constraints").
"""

from __future__ import annotations

import contextvars
import logging
import os
import time
from contextlib import asynccontextmanager
from typing import Any, AsyncIterator

from spring_voyage_agent_sdk.rate_limit import ProgressRateLimiter, default_limiter
from spring_voyage_agent_sdk.telemetry import (
    TelemetryEmitter,
    new_span_id,
    new_trace_id,
)

logger = logging.getLogger("spring-voyage-agent-sdk.runtime_context")

# Event-kind labels used as the rate-limiter discriminator. Keep short
# and lowercase — they ride on the OTLP wire as attribute values.
KIND_PROGRESS = "progress"
KIND_TOOL_CALL = "tool_call"
KIND_LLM_TURN = "llm_turn"
KIND_RESPONSE_DISCIPLINE_VIOLATION = "response_discipline_violation"


# Module-level current-context accessor. The runtime sets this for the
# duration of one ``on_message`` invocation; handlers retrieve it via
# :meth:`RuntimeContext.current`.
_CURRENT: contextvars.ContextVar["RuntimeContext | None"] = contextvars.ContextVar(
    "spring_voyage_agent_sdk.runtime_context.current", default=None
)


def _now_unix_nanos() -> int:
    return time.time_ns()


class ToolCallSpan:
    """Open span for one ``sv.tool.call`` invocation.

    Constructed by :meth:`RuntimeContext.tool_call`; the caller sets
    the result or exception before exit.
    """

    __slots__ = (
        "_name",
        "_args",
        "_trace_id",
        "_span_id",
        "_parent_span_id",
        "_start_nanos",
        "_emitter",
        "_result",
        "_error",
        "_finished",
    )

    def __init__(
        self,
        *,
        name: str,
        args: Any,
        trace_id: str,
        span_id: str,
        parent_span_id: str | None,
        emitter: TelemetryEmitter,
    ) -> None:
        self._name = name
        self._args = args
        self._trace_id = trace_id
        self._span_id = span_id
        self._parent_span_id = parent_span_id
        self._start_nanos = _now_unix_nanos()
        self._emitter = emitter
        self._result: Any = None
        self._error: BaseException | None = None
        self._finished = False

    @property
    def trace_id(self) -> str:
        return self._trace_id

    @property
    def span_id(self) -> str:
        return self._span_id

    def set_result(self, result: Any) -> None:
        """Record the tool's return value; attached to the span on exit."""
        self._result = result

    def set_error(self, error: BaseException) -> None:
        """Record an exception; attached to the span as a status."""
        self._error = error

    def finish(self) -> None:
        """Emit the span. Idempotent — second call is a no-op."""
        if self._finished:
            return
        self._finished = True

        attrs: dict[str, Any] = {"tool.name": self._name}
        if self._args is not None:
            try:
                # Bound the size — large argument blobs would balloon
                # the OTLP payload. The platform-side ingest has its own
                # truncation policy but the SDK applies a defensive cap.
                args_text = str(self._args)
                attrs["tool.args.preview"] = args_text[:512]
                attrs["tool.args.length"] = len(args_text)
            except Exception:  # noqa: BLE001
                pass
        if self._result is not None and self._error is None:
            try:
                result_text = str(self._result)
                attrs["tool.result.preview"] = result_text[:512]
                attrs["tool.result.length"] = len(result_text)
            except Exception:  # noqa: BLE001
                pass

        status_code = 2 if self._error is not None else None
        status_message = repr(self._error) if self._error is not None else None
        if self._error is not None:
            attrs["exception.type"] = type(self._error).__name__
            attrs["exception.message"] = str(self._error)[:512]

        self._emitter.emit_span(
            name="sv.tool.call",
            trace_id=self._trace_id,
            span_id=self._span_id,
            parent_span_id=self._parent_span_id,
            start_unix_nanos=self._start_nanos,
            end_unix_nanos=_now_unix_nanos(),
            attributes=attrs,
            status_code=status_code,
            status_message=status_message,
        )


class LlmTurnSpan:
    """Open span for one ``sv.llm.turn`` LLM invocation.

    Constructed by :meth:`RuntimeContext.llm_turn`. Callers populate
    completion + token counts via :meth:`set_completion`.
    """

    __slots__ = (
        "_model",
        "_prompt",
        "_trace_id",
        "_span_id",
        "_parent_span_id",
        "_start_nanos",
        "_emitter",
        "_completion",
        "_tokens_in",
        "_tokens_out",
        "_error",
        "_finished",
    )

    def __init__(
        self,
        *,
        model: str,
        prompt: str | None,
        trace_id: str,
        span_id: str,
        parent_span_id: str | None,
        emitter: TelemetryEmitter,
    ) -> None:
        self._model = model
        self._prompt = prompt
        self._trace_id = trace_id
        self._span_id = span_id
        self._parent_span_id = parent_span_id
        self._start_nanos = _now_unix_nanos()
        self._emitter = emitter
        self._completion: str | None = None
        self._tokens_in: int | None = None
        self._tokens_out: int | None = None
        self._error: BaseException | None = None
        self._finished = False

    @property
    def trace_id(self) -> str:
        return self._trace_id

    @property
    def span_id(self) -> str:
        return self._span_id

    def set_completion(
        self,
        completion: str | None,
        *,
        tokens_in: int | None = None,
        tokens_out: int | None = None,
    ) -> None:
        """Record the LLM's completion + optional token counts."""
        self._completion = completion
        self._tokens_in = tokens_in
        self._tokens_out = tokens_out

    def set_error(self, error: BaseException) -> None:
        self._error = error

    def finish(self) -> None:
        if self._finished:
            return
        self._finished = True

        attrs: dict[str, Any] = {"llm.model": self._model}
        if self._prompt is not None:
            attrs["llm.prompt.length"] = len(self._prompt)
            attrs["llm.prompt.preview"] = self._prompt[:512]
        if self._completion is not None:
            attrs["llm.completion.length"] = len(self._completion)
            attrs["llm.completion.preview"] = self._completion[:512]
        if self._tokens_in is not None:
            attrs["llm.tokens.input"] = int(self._tokens_in)
        if self._tokens_out is not None:
            attrs["llm.tokens.output"] = int(self._tokens_out)

        status_code = 2 if self._error is not None else None
        status_message = repr(self._error) if self._error is not None else None
        if self._error is not None:
            attrs["exception.type"] = type(self._error).__name__
            attrs["exception.message"] = str(self._error)[:512]

        self._emitter.emit_span(
            name="sv.llm.turn",
            trace_id=self._trace_id,
            span_id=self._span_id,
            parent_span_id=self._parent_span_id,
            start_unix_nanos=self._start_nanos,
            end_unix_nanos=_now_unix_nanos(),
            attributes=attrs,
            status_code=status_code,
            status_message=status_message,
        )


class RuntimeContext:
    """Per-turn telemetry surface bound to one ``on_message`` invocation.

    Holds the active OTel trace + span ids, the OTLP emitter, and the
    rate limiter. Agent authors retrieve the active instance via
    :meth:`current` and call :meth:`report_progress`, :meth:`tool_call`,
    :meth:`llm_turn`.

    Authors must not construct ``RuntimeContext`` directly — the SDK
    runtime owns the lifecycle. A turn that runs outside the SDK (e.g.
    a unit test calling ``on_message`` directly) gets a no-op stub via
    :meth:`null`.
    """

    def __init__(
        self,
        *,
        emitter: TelemetryEmitter,
        rate_limiter: ProgressRateLimiter | None = None,
        trace_id: str | None = None,
        root_span_id: str | None = None,
        thread_id: str | None = None,
        message_id: str | None = None,
    ) -> None:
        self._emitter = emitter
        self._rate_limiter = rate_limiter or default_limiter()
        self._trace_id = trace_id or new_trace_id()
        self._root_span_id = root_span_id or new_span_id()
        self._root_span_start_nanos = _now_unix_nanos()
        self._thread_id = thread_id
        self._message_id = message_id
        self._root_events: list[dict[str, Any]] = []
        self._root_attrs: dict[str, Any] = {}
        self._finished = False
        self._final_response_observed = False

    # ------------------------------------------------------------------
    # ContextVar binding
    # ------------------------------------------------------------------

    @classmethod
    def current(cls) -> "RuntimeContext":
        """Return the active context, or a no-op stub if none is set.

        Agent code calls ``RuntimeContext.current()`` from ``on_message``
        to reach the telemetry primitives. Outside an active turn — in
        unit tests, in ``initialize()``, etc. — the call returns a
        disabled stub whose emission methods are no-ops.
        """
        ctx = _CURRENT.get()
        if ctx is None:
            return _NullRuntimeContext()
        return ctx

    def bind(self) -> contextvars.Token:
        """Bind this context as the active one. Returns a reset token."""
        return _CURRENT.set(self)

    @classmethod
    def unbind(cls, token: contextvars.Token) -> None:
        _CURRENT.reset(token)

    # ------------------------------------------------------------------
    # Public surface
    # ------------------------------------------------------------------

    @property
    def trace_id(self) -> str:
        return self._trace_id

    @property
    def root_span_id(self) -> str:
        return self._root_span_id

    @property
    def subject_uuid(self) -> str:
        return self._emitter.subject_uuid

    @property
    def final_response_observed(self) -> bool:
        return self._final_response_observed

    def mark_final_response_observed(self) -> None:
        """Called by the runtime when the handler yields a final response.

        The safety net uses this flag — if no final response was seen
        when the turn ends, the runtime synthesizes one.
        """
        self._final_response_observed = True

    def report_progress(
        self,
        text: str,
        *,
        kind: str | None = None,
        attrs: dict[str, Any] | None = None,
    ) -> bool:
        """Emit a progress event on the active turn span.

        Best-effort: a tripped rate limiter, a disabled emitter, or a
        transport failure all return ``False`` without raising. Callers
        do not need to handle the return value — the agent's logic flow
        must never depend on telemetry succeeding.
        """
        return self._record_event(
            event_kind=KIND_PROGRESS,
            message=text,
            attrs={"kind": kind, **(attrs or {})} if kind else (attrs or {}),
        )

    def tool_call(self, name: str, args: Any = None) -> "_ToolCallContextManager":
        """Open an OTel span for one tool call. Use as ``async with``."""
        return _ToolCallContextManager(self, name=name, args=args)

    def llm_turn(self, model: str, prompt: str | None = None) -> "_LlmTurnContextManager":
        """Open an OTel span for one LLM turn. Use as ``async with``."""
        return _LlmTurnContextManager(self, model=model, prompt=prompt)

    def emit_response_discipline_violation(self, *, reason: str) -> None:
        """Emit the structural marker that the safety net synthesised a reply.

        Unlike :meth:`report_progress` this bypasses the rate limiter —
        a response-discipline violation is a structural event, not a
        narrative beat, and we want it on the bus even when the same
        misbehaving handler also flooded the limiter.
        """
        self._record_event(
            event_kind=KIND_RESPONSE_DISCIPLINE_VIOLATION,
            message=reason,
            attrs={"kind": KIND_RESPONSE_DISCIPLINE_VIOLATION},
            bypass_rate_limit=True,
        )

    # ------------------------------------------------------------------
    # Internal helpers
    # ------------------------------------------------------------------

    def _record_event(
        self,
        *,
        event_kind: str,
        message: str,
        attrs: dict[str, Any] | None = None,
        bypass_rate_limit: bool = False,
    ) -> bool:
        if not bypass_rate_limit:
            if not self._rate_limiter.try_acquire(self.subject_uuid, event_kind):
                self._rate_limiter.log_drop(self.subject_uuid, event_kind)
                return False

        event = TelemetryEmitter.progress_event(message, kind=event_kind, attrs=attrs)
        # Attach to the active root span — same trace + parent.
        self._root_events.append(event)

        # Stream the event by closing & re-opening the root span — we
        # emit incrementally per beat rather than only at turn end so the
        # portal/CLI live-tail (#2492) sees progress in real time.
        return self._emit_root_span_snapshot()

    def _emit_root_span_snapshot(self) -> bool:
        """Ship the current turn-root span with accumulated events.

        Each emission is a complete OTLP envelope with the same span id
        — the ingest's de-dup behaviour treats subsequent emissions as
        updates. This is best-effort: a failure logs at debug and
        returns ``False`` without raising.
        """
        return self._emitter.emit_span(
            name="sv.agent.turn",
            trace_id=self._trace_id,
            span_id=self._root_span_id,
            parent_span_id=None,
            start_unix_nanos=self._root_span_start_nanos,
            end_unix_nanos=_now_unix_nanos(),
            attributes={
                **self._root_attrs,
                "sv.thread.id": self._thread_id or "",
                "sv.message.id": self._message_id or "",
            },
            events=list(self._root_events),
        )

    def finish(self) -> None:
        """Close the turn — emit one final root-span snapshot."""
        if self._finished:
            return
        self._finished = True
        self._emit_root_span_snapshot()


class _NullRuntimeContext(RuntimeContext):
    """No-op context returned when ``RuntimeContext.current()`` is unbound.

    All emission methods short-circuit so unit tests that exercise an
    ``on_message`` outside the SDK do not need to set up an OTLP
    endpoint.
    """

    def __init__(self) -> None:  # noqa: D401 — intentional no-op
        # Skip the parent's network-touching init.
        self._emitter = _NullEmitter()
        self._rate_limiter = default_limiter()
        self._trace_id = "0" * 32
        self._root_span_id = "0" * 16
        self._root_span_start_nanos = _now_unix_nanos()
        self._thread_id = None
        self._message_id = None
        self._root_events = []
        self._root_attrs = {}
        self._finished = True
        self._final_response_observed = False

    def _record_event(self, **_: Any) -> bool:  # type: ignore[override]
        return False

    def _emit_root_span_snapshot(self) -> bool:  # type: ignore[override]
        return False


class _NullEmitter(TelemetryEmitter):
    """Disabled emitter — the stub context's underlying transport."""

    def __init__(self) -> None:  # noqa: D401 — intentional no-op
        super().__init__(endpoint=None)


class _ToolCallContextManager:
    """Async context manager wrapping one :class:`ToolCallSpan`."""

    __slots__ = ("_context", "_name", "_args", "_span")

    def __init__(self, context: RuntimeContext, *, name: str, args: Any) -> None:
        self._context = context
        self._name = name
        self._args = args
        self._span: ToolCallSpan | None = None

    async def __aenter__(self) -> ToolCallSpan:
        if not self._context._rate_limiter.try_acquire(
            self._context.subject_uuid, KIND_TOOL_CALL
        ):
            # The rate limiter is shared with progress events, but
            # tool-call spans are still emitted lazily on a separate
            # bucket. If we got rate-limited, hand the caller a span
            # whose finish is a no-op (its emitter is disabled).
            self._context._rate_limiter.log_drop(
                self._context.subject_uuid, KIND_TOOL_CALL
            )
            self._span = ToolCallSpan(
                name=self._name,
                args=self._args,
                trace_id=self._context.trace_id,
                span_id=new_span_id(),
                parent_span_id=self._context.root_span_id,
                emitter=_NullEmitter(),
            )
            return self._span

        self._span = ToolCallSpan(
            name=self._name,
            args=self._args,
            trace_id=self._context.trace_id,
            span_id=new_span_id(),
            parent_span_id=self._context.root_span_id,
            emitter=self._context._emitter,
        )
        return self._span

    async def __aexit__(self, exc_type, exc, tb) -> None:
        if self._span is None:
            return
        if exc is not None and self._span._error is None:
            self._span.set_error(exc)
        self._span.finish()


class _LlmTurnContextManager:
    """Async context manager wrapping one :class:`LlmTurnSpan`."""

    __slots__ = ("_context", "_model", "_prompt", "_span")

    def __init__(self, context: RuntimeContext, *, model: str, prompt: str | None) -> None:
        self._context = context
        self._model = model
        self._prompt = prompt
        self._span: LlmTurnSpan | None = None

    async def __aenter__(self) -> LlmTurnSpan:
        if not self._context._rate_limiter.try_acquire(
            self._context.subject_uuid, KIND_LLM_TURN
        ):
            self._context._rate_limiter.log_drop(
                self._context.subject_uuid, KIND_LLM_TURN
            )
            self._span = LlmTurnSpan(
                model=self._model,
                prompt=self._prompt,
                trace_id=self._context.trace_id,
                span_id=new_span_id(),
                parent_span_id=self._context.root_span_id,
                emitter=_NullEmitter(),
            )
            return self._span

        self._span = LlmTurnSpan(
            model=self._model,
            prompt=self._prompt,
            trace_id=self._context.trace_id,
            span_id=new_span_id(),
            parent_span_id=self._context.root_span_id,
            emitter=self._context._emitter,
        )
        return self._span

    async def __aexit__(self, exc_type, exc, tb) -> None:
        if self._span is None:
            return
        if exc is not None and self._span._error is None:
            self._span.set_error(exc)
        self._span.finish()


# Top-level convenience accessors mirroring the issue body's design.
# They forward to ``RuntimeContext.current()`` so an author can write
# ``await report_progress("…")`` without touching the context object.


def report_progress(text: str, *, kind: str | None = None, attrs: dict[str, Any] | None = None) -> bool:
    return RuntimeContext.current().report_progress(text, kind=kind, attrs=attrs)


def tool_call(name: str, args: Any = None) -> _ToolCallContextManager:
    return RuntimeContext.current().tool_call(name, args)


def llm_turn(model: str, prompt: str | None = None) -> _LlmTurnContextManager:
    return RuntimeContext.current().llm_turn(model, prompt)
