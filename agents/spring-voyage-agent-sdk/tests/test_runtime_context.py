"""Tests for :class:`RuntimeContext` and the response-discipline safety net (#2493)."""

from __future__ import annotations

import asyncio
from typing import Any
from unittest.mock import AsyncMock, MagicMock

import pytest

from spring_voyage_agent_sdk.hooks import AgentHooks
from spring_voyage_agent_sdk.rate_limit import (
    ProgressRateLimiter,
    reset_default_limiter,
)
from spring_voyage_agent_sdk.runtime import (
    SAFETY_NET_REPLY,
    _SdkAgentExecutor,
)
from spring_voyage_agent_sdk.runtime_context import (
    KIND_RESPONSE_DISCIPLINE_VIOLATION,
    RuntimeContext,
)
from spring_voyage_agent_sdk.telemetry import TelemetryEmitter
from spring_voyage_agent_sdk.types import Message, Response


class _RecordingEmitter(TelemetryEmitter):
    """Test double — captures every emit_span / emit_log call."""

    def __init__(self) -> None:
        super().__init__(
            endpoint="https://test.example/otlp",
            resource_attributes={
                "sv.tenant.id": "t1",
                "sv.subject.uuid": "s1",
                "sv.subject.kind": "agent",
            },
        )
        self.spans: list[dict[str, Any]] = []
        self.logs: list[dict[str, Any]] = []

    def emit_span(self, **kwargs: Any) -> bool:  # type: ignore[override]
        self.spans.append(kwargs)
        return True

    def emit_log(self, **kwargs: Any) -> bool:  # type: ignore[override]
        self.logs.append(kwargs)
        return True


@pytest.fixture(autouse=True)
def _reset_limiter():
    """Each test starts with a fresh shared limiter."""
    reset_default_limiter()
    yield
    reset_default_limiter()


class TestRuntimeContext:
    def test_current_returns_null_when_unbound(self):
        ctx = RuntimeContext.current()
        # Null context's emissions are no-ops returning False.
        assert ctx.report_progress("hello") is False

    @pytest.mark.asyncio
    async def test_bound_context_is_visible_to_handler(self):
        emitter = _RecordingEmitter()
        ctx = RuntimeContext(emitter=emitter)
        token = ctx.bind()
        try:
            visible = RuntimeContext.current()
            assert visible is ctx
        finally:
            RuntimeContext.unbind(token)

        assert RuntimeContext.current() is not ctx

    @pytest.mark.asyncio
    async def test_report_progress_emits_root_span_with_event(self):
        emitter = _RecordingEmitter()
        ctx = RuntimeContext(
            emitter=emitter,
            rate_limiter=ProgressRateLimiter(rate_per_second=100, burst=100),
        )
        token = ctx.bind()
        try:
            assert ctx.report_progress("starting work") is True
        finally:
            RuntimeContext.unbind(token)
            ctx.finish()

        # At least one span emission with an sv.progress event.
        progress_spans = [
            s
            for s in emitter.spans
            if any(e.get("name") == "sv.progress" for e in s.get("events", []))
        ]
        assert progress_spans, "Expected at least one span carrying an sv.progress event"
        # The event's attributes come back as the KV list shape because
        # ``TelemetryEmitter.progress_event`` converts before attaching.
        first_event = progress_spans[0]["events"][0]
        attrs = {a["key"]: a["value"] for a in first_event["attributes"]}
        assert attrs["message"]["stringValue"] == "starting work"

    @pytest.mark.asyncio
    async def test_tool_call_emits_span(self):
        emitter = _RecordingEmitter()
        ctx = RuntimeContext(
            emitter=emitter,
            rate_limiter=ProgressRateLimiter(rate_per_second=100, burst=100),
        )
        token = ctx.bind()
        try:
            async with ctx.tool_call("acme.echo", {"text": "hi"}) as span:
                span.set_result({"echo": "hi"})
        finally:
            RuntimeContext.unbind(token)
            ctx.finish()

        tool_spans = [s for s in emitter.spans if s["name"] == "sv.tool.call"]
        assert len(tool_spans) == 1
        # The recording emitter receives ``attributes`` as a plain dict
        # (we override emit_span before the OTLP KV-list conversion).
        attrs = tool_spans[0]["attributes"]
        assert attrs["tool.name"] == "acme.echo"

    @pytest.mark.asyncio
    async def test_tool_call_records_exception(self):
        emitter = _RecordingEmitter()
        ctx = RuntimeContext(
            emitter=emitter,
            rate_limiter=ProgressRateLimiter(rate_per_second=100, burst=100),
        )
        token = ctx.bind()
        try:
            with pytest.raises(RuntimeError):
                async with ctx.tool_call("acme.fail", None):
                    raise RuntimeError("boom")
        finally:
            RuntimeContext.unbind(token)
            ctx.finish()

        tool_spans = [s for s in emitter.spans if s["name"] == "sv.tool.call"]
        assert len(tool_spans) == 1
        # The status code is 2 (error) per OTLP convention.
        assert tool_spans[0].get("status_code") == 2

    @pytest.mark.asyncio
    async def test_llm_turn_records_completion_and_tokens(self):
        emitter = _RecordingEmitter()
        ctx = RuntimeContext(
            emitter=emitter,
            rate_limiter=ProgressRateLimiter(rate_per_second=100, burst=100),
        )
        token = ctx.bind()
        try:
            async with ctx.llm_turn("claude-sonnet", prompt="hello") as span:
                span.set_completion("hello back", tokens_in=2, tokens_out=2)
        finally:
            RuntimeContext.unbind(token)
            ctx.finish()

        llm_spans = [s for s in emitter.spans if s["name"] == "sv.llm.turn"]
        assert len(llm_spans) == 1
        attrs = llm_spans[0]["attributes"]
        assert attrs["llm.model"] == "claude-sonnet"
        assert attrs["llm.tokens.input"] == 2
        assert attrs["llm.tokens.output"] == 2

    @pytest.mark.asyncio
    async def test_emit_response_discipline_violation_bypasses_rate_limit(self):
        # Use an aggressive limiter that would normally block.
        emitter = _RecordingEmitter()
        limiter = ProgressRateLimiter(rate_per_second=0.01, burst=1)
        # Drain the bucket.
        limiter.try_acquire("s1", KIND_RESPONSE_DISCIPLINE_VIOLATION)
        ctx = RuntimeContext(emitter=emitter, rate_limiter=limiter)
        token = ctx.bind()
        try:
            ctx.emit_response_discipline_violation(reason="forgot to reply")
        finally:
            RuntimeContext.unbind(token)
            ctx.finish()

        violation_events = [
            e
            for s in emitter.spans
            for e in s.get("events", [])
            if e.get("name") == "sv.progress"
            and any(
                a["key"] == "kind"
                and a["value"].get("stringValue") == KIND_RESPONSE_DISCIPLINE_VIOLATION
                for a in e.get("attributes", [])
            )
        ]
        assert len(violation_events) >= 1

    @pytest.mark.asyncio
    async def test_progress_rate_limited_drops_excess(self):
        emitter = _RecordingEmitter()
        limiter = ProgressRateLimiter(rate_per_second=0.01, burst=3)
        ctx = RuntimeContext(emitter=emitter, rate_limiter=limiter)
        token = ctx.bind()
        try:
            results = [ctx.report_progress(f"beat {i}") for i in range(100)]
        finally:
            RuntimeContext.unbind(token)
            ctx.finish()

        # At most burst=3 succeed; the rest are dropped.
        assert sum(results) <= 3
        # The dropped ones must NOT have raised — best-effort emission.
        assert all(isinstance(r, bool) for r in results)


def _make_context_with_message(text: str = "hello"):
    """Minimal RequestContext stub for _SdkAgentExecutor tests."""
    from types import SimpleNamespace

    part = SimpleNamespace(root=SimpleNamespace(kind="text", text=text))
    message = SimpleNamespace(role="user", parts=[part])
    ctx = MagicMock()
    ctx.task_id = "t1"
    ctx.context_id = "ctx1"
    ctx.message = message
    ctx.current_task = MagicMock()
    return ctx


def _make_event_queue():
    eq = MagicMock()
    eq.enqueue_event = AsyncMock()
    return eq


def _make_hooks(on_message):
    async def _noop_init(_ctx):
        pass

    async def _noop_shutdown(_reason):
        pass

    return AgentHooks(initialize=_noop_init, on_message=on_message, on_shutdown=_noop_shutdown)


class TestSafetyNet:
    """End-to-end safety-net behaviour through _SdkAgentExecutor.

    The original silent-success failure mode (issue #2493) is: a
    handler runs work but never yields a final Response. Pre-#2493 the
    SDK failed the task; post-#2493 the SDK ships a synthetic reply
    plus a violation event.
    """

    @pytest.mark.asyncio
    async def test_handler_without_final_response_synthesizes_reply(
        self, caplog: pytest.LogCaptureFixture
    ):
        async def on_message(_message: Message):
            # Pretend to do work but never yield.
            return
            yield  # noqa: E0001 — make this an async generator

        done = asyncio.Event()
        done.set()
        executor = _SdkAgentExecutor(
            hooks=_make_hooks(on_message),
            concurrent_threads=True,
            initialize_done=done,
        )

        ctx = _make_context_with_message()
        eq = _make_event_queue()

        import logging

        with caplog.at_level(logging.WARNING, logger="spring-voyage-agent-sdk.runtime"):
            await executor.execute(ctx, eq)

        # The stock reply was added as an artifact.
        artifact_calls = [
            call for call in eq.enqueue_event.call_args_list
            if "artifact" in type(call.args[0]).__name__.lower()
        ]
        assert artifact_calls, "Expected the safety net to emit a final artifact"
        # The task was completed (not failed).
        from a2a.types import TaskState, TaskStatusUpdateEvent

        last = eq.enqueue_event.call_args_list[-1].args[0]
        assert isinstance(last, TaskStatusUpdateEvent)
        assert last.status.state == TaskState.TASK_STATE_COMPLETED
        # Warning logged.
        assert any(
            "Response-discipline violation" in rec.getMessage()
            for rec in caplog.records
        )

    @pytest.mark.asyncio
    async def test_handler_with_final_response_skips_safety_net(self):
        async def on_message(_message: Message):
            yield Response(text="hello", final=True)

        done = asyncio.Event()
        done.set()
        executor = _SdkAgentExecutor(
            hooks=_make_hooks(on_message),
            concurrent_threads=True,
            initialize_done=done,
        )
        ctx = _make_context_with_message()
        eq = _make_event_queue()
        await executor.execute(ctx, eq)

        # The artifact reflects the handler's text, not the stock reply.
        from a2a.server.events import Event  # noqa: F401 — imports for typing

        artifact_events = [
            call.args[0]
            for call in eq.enqueue_event.call_args_list
            if "artifact" in type(call.args[0]).__name__.lower()
        ]
        assert artifact_events
        # The artifact carries "hello", not SAFETY_NET_REPLY.
        artifact = artifact_events[0]
        artifact_text = "".join(p.text for p in artifact.artifact.parts if p.text)
        assert "hello" in artifact_text
        assert SAFETY_NET_REPLY not in artifact_text
