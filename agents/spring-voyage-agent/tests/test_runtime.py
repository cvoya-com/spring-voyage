"""Tests for the SDK runtime — _SdkAgentExecutor and lifecycle ordering.

Covers:
  - initialize must complete before on_message (spec §1.1)
  - async-generator and coroutine on_message paths
  - error chunk terminates stream with failed status
  - concurrent_threads=False serialisation lock
  - cancel enqueues canceled status
  - bare-startup (no env vars): _load_and_initialize logs a warning and
    leaves _initialize_done unset without crashing (smoke-test contract)
"""

from __future__ import annotations

import asyncio
import os
from types import SimpleNamespace
from unittest.mock import AsyncMock, MagicMock

import pytest

from spring_voyage_agent.hooks import AgentHooks
from spring_voyage_agent.runtime import AgentRuntime, _SdkAgentExecutor
from spring_voyage_agent.types import Message, Response


def _make_hooks(
    on_message=None,
    initialize=None,
    on_shutdown=None,
) -> AgentHooks:
    async def _noop_initialize(ctx):
        pass

    async def _noop_shutdown(reason):
        pass

    async def _default_on_message(message: Message):
        yield Response(text="default response", final=True)

    return AgentHooks(
        initialize=initialize or _noop_initialize,
        on_message=on_message or _default_on_message,
        on_shutdown=on_shutdown or _noop_shutdown,
    )


def _make_context(*, task_id="t1", context_id="ctx1", text="hello"):
    """Build a minimal RequestContext-like mock."""
    part = SimpleNamespace(root=SimpleNamespace(kind="text", text=text))
    message = SimpleNamespace(
        role="user",
        parts=[part],
    )
    ctx = MagicMock()
    ctx.task_id = task_id
    ctx.context_id = context_id
    ctx.message = message
    ctx.current_task = MagicMock()
    return ctx


def _make_event_queue():
    eq = MagicMock()
    eq.enqueue_event = AsyncMock()
    return eq


class TestSdkAgentExecutor:
    @pytest.mark.asyncio
    async def test_async_generator_hook_produces_completed_status(self):
        """Happy path: async-generator on_message yields text → completed."""

        async def on_message(message: Message):
            yield Response(text="hello agent")

        done_event = asyncio.Event()
        done_event.set()

        executor = _SdkAgentExecutor(
            hooks=_make_hooks(on_message=on_message),
            concurrent_threads=True,
            initialize_done=done_event,
        )

        ctx = _make_context()
        eq = _make_event_queue()

        await executor.execute(ctx, eq)

        # Expect: task, working, artifact, completed — 4 events.
        assert eq.enqueue_event.call_count == 4
        # Last event is completed status.
        last_call = eq.enqueue_event.call_args_list[-1][0][0]
        from a2a.types import TaskState, TaskStatusUpdateEvent

        assert isinstance(last_call, TaskStatusUpdateEvent)
        assert last_call.status.state == TaskState.completed

    @pytest.mark.asyncio
    async def test_coroutine_hook_produces_completed_status(self):
        """on_message as a plain coroutine returning a string is supported."""

        async def on_message(message: Message):
            return "coroutine result"

        done_event = asyncio.Event()
        done_event.set()

        executor = _SdkAgentExecutor(
            hooks=_make_hooks(on_message=on_message),
            concurrent_threads=True,
            initialize_done=done_event,
        )

        ctx = _make_context()
        eq = _make_event_queue()

        await executor.execute(ctx, eq)

        from a2a.types import TaskState, TaskStatusUpdateEvent

        last_call = eq.enqueue_event.call_args_list[-1][0][0]
        assert isinstance(last_call, TaskStatusUpdateEvent)
        assert last_call.status.state == TaskState.completed

    @pytest.mark.asyncio
    async def test_error_response_produces_failed_status(self):
        """Yielding a Response with error= terminates with failed status."""

        async def on_message(message: Message):
            yield Response(error="something broke")

        done_event = asyncio.Event()
        done_event.set()

        executor = _SdkAgentExecutor(
            hooks=_make_hooks(on_message=on_message),
            concurrent_threads=True,
            initialize_done=done_event,
        )

        ctx = _make_context()
        eq = _make_event_queue()

        await executor.execute(ctx, eq)

        from a2a.types import TaskState, TaskStatusUpdateEvent

        last_call = eq.enqueue_event.call_args_list[-1][0][0]
        assert isinstance(last_call, TaskStatusUpdateEvent)
        assert last_call.status.state == TaskState.failed

    @pytest.mark.asyncio
    async def test_unhandled_exception_produces_failed_status(self):
        """An uncaught exception in on_message surfaces as failed status."""

        async def on_message(message: Message):
            raise RuntimeError("boom")
            yield  # make it a generator

        done_event = asyncio.Event()
        done_event.set()

        executor = _SdkAgentExecutor(
            hooks=_make_hooks(on_message=on_message),
            concurrent_threads=True,
            initialize_done=done_event,
        )

        ctx = _make_context()
        eq = _make_event_queue()

        await executor.execute(ctx, eq)

        from a2a.types import TaskState, TaskStatusUpdateEvent

        last_call = eq.enqueue_event.call_args_list[-1][0][0]
        assert isinstance(last_call, TaskStatusUpdateEvent)
        assert last_call.status.state == TaskState.failed

    @pytest.mark.asyncio
    async def test_on_message_waits_for_initialize(self):
        """Spec §1.1: on_message must not run before initialize completes."""
        order: list[str] = []

        async def on_message(message: Message):
            order.append("on_message")
            yield Response(text="done")

        # Start with the event unset.
        done_event = asyncio.Event()

        executor = _SdkAgentExecutor(
            hooks=_make_hooks(on_message=on_message),
            concurrent_threads=True,
            initialize_done=done_event,
        )

        ctx = _make_context()
        eq = _make_event_queue()

        # Launch execute — it should block waiting for initialize_done.
        task = asyncio.create_task(executor.execute(ctx, eq))

        # Give the task a moment to reach the wait.
        await asyncio.sleep(0)

        # on_message should not have run yet.
        assert "on_message" not in order

        # Now signal initialize done.
        done_event.set()
        await task

        assert "on_message" in order

    @pytest.mark.asyncio
    async def test_cancel_enqueues_canceled_status(self):
        done_event = asyncio.Event()
        done_event.set()

        executor = _SdkAgentExecutor(
            hooks=_make_hooks(),
            concurrent_threads=True,
            initialize_done=done_event,
        )

        ctx = _make_context()
        eq = _make_event_queue()

        await executor.cancel(ctx, eq)

        from a2a.types import TaskState, TaskStatusUpdateEvent

        assert eq.enqueue_event.call_count == 1
        call = eq.enqueue_event.call_args_list[0][0][0]
        assert isinstance(call, TaskStatusUpdateEvent)
        assert call.status.state == TaskState.canceled

    @pytest.mark.asyncio
    async def test_concurrent_threads_false_serialises(self):
        """concurrent_threads=False → serial_lock is present."""
        done_event = asyncio.Event()
        done_event.set()

        executor = _SdkAgentExecutor(
            hooks=_make_hooks(),
            concurrent_threads=False,
            initialize_done=done_event,
        )

        assert executor._serial_lock is not None

    @pytest.mark.asyncio
    async def test_concurrent_threads_true_no_lock(self):
        """concurrent_threads=True → serial_lock is None (no serialisation)."""
        done_event = asyncio.Event()
        done_event.set()

        executor = _SdkAgentExecutor(
            hooks=_make_hooks(),
            concurrent_threads=True,
            initialize_done=done_event,
        )

        assert executor._serial_lock is None


class TestBareStartup:
    """Verify the uvicorn-first startup contract.

    The smoke-test harness starts the dapr-agent container with *no*
    platform env vars set.  The SDK must:
      1. Not crash when IAgentContext.load() raises ContextLoadError.
      2. Leave _initialize_done unset (agent card reachable, on_message
         blocked — but no crash).

    This is the unit-level analogue of the container smoke test.
    """

    @pytest.mark.asyncio
    async def test_load_and_initialize_no_env_vars_leaves_done_unset(self, monkeypatch):
        """Without platform env vars, _load_and_initialize warns and returns
        without setting _initialize_done or raising."""
        # Strip all SPRING_* vars from the environment.
        spring_vars = [k for k in os.environ if k.startswith("SPRING_")]
        for v in spring_vars:
            monkeypatch.delenv(v, raising=False)

        async def _noop_initialize(ctx):
            pass

        async def _noop_on_message(msg):
            yield Response(text="ok")

        async def _noop_shutdown(reason):
            pass

        hooks = AgentHooks(
            initialize=_noop_initialize,
            on_message=_noop_on_message,
            on_shutdown=_noop_shutdown,
        )
        runtime = AgentRuntime(hooks, port=0)

        executor = _SdkAgentExecutor(
            hooks=hooks,
            concurrent_threads=True,
            initialize_done=runtime._initialize_done,
        )

        # _load_and_initialize must complete without raising.
        await runtime._load_and_initialize(executor)

        # _initialize_done must NOT be set — no context, no initialize call.
        assert not runtime._initialize_done.is_set()

    @pytest.mark.asyncio
    async def test_load_and_initialize_with_context_sets_done(self, monkeypatch):
        """When all required env vars are present, _load_and_initialize calls
        initialize() and sets _initialize_done."""
        required = {
            "SPRING_TENANT_ID": "t1",
            "SPRING_AGENT_ID": "a1",
            "SPRING_BUCKET2_URL": "http://b2",
            "SPRING_BUCKET2_TOKEN": "tok",
            "SPRING_LLM_PROVIDER_URL": "http://llm",
            "SPRING_LLM_PROVIDER_TOKEN": "llmtok",
            "SPRING_MCP_URL": "http://mcp",
            "SPRING_MCP_TOKEN": "mcptok",
            "SPRING_TELEMETRY_URL": "http://tel",
            "SPRING_WORKSPACE_PATH": "/tmp/ws",
            "SPRING_CONCURRENT_THREADS": "true",
        }
        for k, v in required.items():
            monkeypatch.setenv(k, v)

        initialized_with: list = []

        async def _record_initialize(ctx):
            initialized_with.append(ctx)

        async def _noop_on_message(msg):
            yield Response(text="ok")

        async def _noop_shutdown(reason):
            pass

        hooks = AgentHooks(
            initialize=_record_initialize,
            on_message=_noop_on_message,
            on_shutdown=_noop_shutdown,
        )
        runtime = AgentRuntime(hooks, port=0)

        executor = _SdkAgentExecutor(
            hooks=hooks,
            concurrent_threads=True,
            initialize_done=runtime._initialize_done,
        )

        await runtime._load_and_initialize(executor)

        # initialize() must have been called and _initialize_done must be set.
        assert runtime._initialize_done.is_set()
        assert len(initialized_with) == 1
        assert initialized_with[0].tenant_id == "t1"
