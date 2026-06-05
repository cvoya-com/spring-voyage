"""Tests for the SDK runtime — _SdkAgentExecutor and lifecycle ordering.

Covers:
  - initialize must complete before on_message (spec §1.1)
  - async-generator and coroutine on_message paths
  - error chunk terminates stream with failed status
  - concurrent_threads=False serialisation lock
  - cancel enqueues canceled status
  - bare-startup (no env vars): _load_and_initialize logs a warning and
    leaves _initialize_done unset without crashing (smoke-test contract)

a2a-sdk 1.x migration (issue #940): TaskState enum values are now
TASK_STATE_COMPLETED / TASK_STATE_FAILED / TASK_STATE_CANCELED (protobuf
style) rather than the 0.3.x aliased names.
"""

from __future__ import annotations

import asyncio
import os
from types import SimpleNamespace
from unittest.mock import AsyncMock, MagicMock

import pytest

from spring_voyage_agent_sdk.hooks import AgentHooks
from spring_voyage_agent_sdk.runtime import AgentRuntime, _SdkAgentExecutor
from spring_voyage_agent_sdk.types import Message, Response


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
    """Build a minimal RequestContext-like mock.

    Parts use the old SimpleNamespace shape (root.text) which Message.text
    still handles via its fallback ``getattr(part, 'root', part)`` path.
    """
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

        # Expect: submitted, working, artifact, completed — 4 events.
        assert eq.enqueue_event.call_count == 4
        # Last event is completed status.
        last_call = eq.enqueue_event.call_args_list[-1][0][0]
        from a2a.types import TaskState, TaskStatusUpdateEvent

        assert isinstance(last_call, TaskStatusUpdateEvent)
        assert last_call.status.state == TaskState.TASK_STATE_COMPLETED

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
        assert last_call.status.state == TaskState.TASK_STATE_COMPLETED

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
        assert last_call.status.state == TaskState.TASK_STATE_FAILED

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
        assert last_call.status.state == TaskState.TASK_STATE_FAILED

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
        assert call.status.state == TaskState.TASK_STATE_CANCELED

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

    @pytest.mark.asyncio
    async def test_runs_bootstrap_integrity_check_before_on_message(self):
        """#2734: when a BootstrapFetcher is attached, the executor
        calls integrity_check_and_refresh() before invoking on_message
        so operator edits to the agent definition land before the
        handler reads context.system_prompt."""
        from spring_voyage_agent_sdk.bootstrap import IntegrityCheckResult

        done_event = asyncio.Event()
        done_event.set()

        invocations: list[str] = []

        async def on_message(message: Message):
            invocations.append("on_message")
            yield Response(text="ok")

        fetcher = MagicMock()
        fetcher.integrity_check_and_refresh = MagicMock(
            side_effect=lambda: (invocations.append("integrity_check"), IntegrityCheckResult(checked=True))[1]
        )

        executor = _SdkAgentExecutor(
            hooks=_make_hooks(on_message=on_message),
            concurrent_threads=True,
            initialize_done=done_event,
            bootstrap_fetcher=fetcher,
        )

        await executor.execute(_make_context(), _make_event_queue())

        assert invocations == ["integrity_check", "on_message"]

    @pytest.mark.asyncio
    async def test_integrity_check_failure_does_not_abort_turn(self):
        """A bootstrap fetcher exception during the per-turn check is
        logged but never raised — the agent must still get a chance to
        answer the message with the last-known-good bundle bytes."""
        done_event = asyncio.Event()
        done_event.set()

        on_message_called = False

        async def on_message(message: Message):
            nonlocal on_message_called
            on_message_called = True
            yield Response(text="ok")

        fetcher = MagicMock()
        fetcher.integrity_check_and_refresh = MagicMock(side_effect=RuntimeError("worker dropped the connection"))

        executor = _SdkAgentExecutor(
            hooks=_make_hooks(on_message=on_message),
            concurrent_threads=True,
            initialize_done=done_event,
            bootstrap_fetcher=fetcher,
        )

        await executor.execute(_make_context(), _make_event_queue())

        assert on_message_called is True

    @pytest.mark.asyncio
    async def test_no_fetcher_skips_integrity_check(self):
        """When no BootstrapFetcher is attached (smoke-test harness,
        alternate launcher) the executor must NOT touch a fetcher.
        This is the no-op path."""
        done_event = asyncio.Event()
        done_event.set()

        executor = _SdkAgentExecutor(
            hooks=_make_hooks(),
            concurrent_threads=True,
            initialize_done=done_event,
            bootstrap_fetcher=None,
        )

        # Calling _maybe_refresh_bootstrap directly is the most direct
        # way to assert the no-op behaviour without standing up an
        # entire A2A request roundtrip.
        await executor._maybe_refresh_bootstrap()


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

    @pytest.mark.asyncio
    async def test_load_and_initialize_pulls_bootstrap_when_url_present(self, monkeypatch, tmp_path):
        """#2734: when SPRING_BOOTSTRAP_URL is set, _load_and_initialize
        pulls the bootstrap bundle before IAgentContext.load() so the
        launcher-contributed `.spring/system-prompt.md` is on disk when
        the loader reads it. Validates the end-to-end wire: the
        launcher's bundle file becomes context.system_prompt."""
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
            "SPRING_WORKSPACE_PATH": str(tmp_path),
            "SPRING_CONCURRENT_THREADS": "true",
            # The bootstrap env vars the launcher (and AgentContextBuilder)
            # stamp on every dispatch per ADR-0055 §9.
            "SPRING_BOOTSTRAP_URL": "http://worker/v1/bootstrap/agents/a1",
            "SPRING_BOOTSTRAP_TOKEN": "bootstrap-bearer",
        }
        for k, v in required.items():
            monkeypatch.setenv(k, v)

        import json as _json

        # Patch the bootstrap fetcher's HTTP impl so the test doesn't
        # need a real worker — return a bundle carrying the assembled
        # system prompt at the canonical path.
        body = _json.dumps(
            {
                "version": "sha256:test",
                "issuedAt": "2026-05-22T12:00:00Z",
                "files": [
                    {
                        "path": ".spring/system-prompt.md",
                        "sha256": "sha256:fake",
                        "content": "Assembled system prompt for the test.",
                    }
                ],
                "platformFileHashes": {".spring/system-prompt.md": "sha256:fake"},
            }
        )

        def _fake_fetch(url, headers, timeout):
            assert url == "http://worker/v1/bootstrap/agents/a1"
            assert headers["Authorization"] == "Bearer bootstrap-bearer"
            return (200, '"sha256:test"', body)

        # Patch the module-level _default_fetch so create_from_env()
        # composes a fetcher that uses our stub.
        import spring_voyage_agent_sdk.bootstrap as bootstrap_module

        monkeypatch.setattr(bootstrap_module, "_default_fetch", _fake_fetch)

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

        # The bootstrap fetch wrote the file under the workspace mount.
        assert (tmp_path / ".spring" / "system-prompt.md").exists()
        # The SDK then loaded context, which read the file into
        # system_prompt. initialize() saw the assembled prompt.
        assert runtime._initialize_done.is_set()
        assert len(initialized_with) == 1
        assert initialized_with[0].system_prompt == "Assembled system prompt for the test."

    @pytest.mark.asyncio
    async def test_load_and_initialize_warns_on_bootstrap_failure(self, monkeypatch, tmp_path):
        """A bootstrap fetch failure leaves _initialize_done unset so
        on_message blocks — the agent never dispatches with a
        half-populated workspace."""
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
            "SPRING_WORKSPACE_PATH": str(tmp_path),
            "SPRING_CONCURRENT_THREADS": "true",
            "SPRING_BOOTSTRAP_URL": "http://worker/v1/bootstrap/agents/a1",
            "SPRING_BOOTSTRAP_TOKEN": "bootstrap-bearer",
        }
        for k, v in required.items():
            monkeypatch.setenv(k, v)

        def _failing_fetch(url, headers, timeout):
            return (500, None, "worker on fire")

        import spring_voyage_agent_sdk.bootstrap as bootstrap_module

        monkeypatch.setattr(bootstrap_module, "_default_fetch", _failing_fetch)

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

        # Must complete without raising — the failure path logs a
        # warning and returns early.
        await runtime._load_and_initialize(executor)

        assert not runtime._initialize_done.is_set()
        assert initialized_with == []


class TestAgentCardRoutes:
    """Regression: both /.well-known/agent-card.json (SDK 1.x canonical) and
    /.well-known/agent.json (legacy alias the smoke contract relies on) must
    return 200. They are easy to break by route ordering — create_rest_routes
    registers a /{tenant}/{path:.*} mount that swallows every two-segment path,
    so the agent-card routes have to come first.
    """

    def test_well_known_paths_return_200(self):
        from a2a.server.events import InMemoryQueueManager
        from a2a.server.request_handlers import DefaultRequestHandler
        from a2a.server.routes import (
            create_agent_card_routes,
            create_rest_routes,
        )
        from a2a.server.tasks import InMemoryTaskStore
        from starlette.applications import Starlette
        from starlette.testclient import TestClient

        from spring_voyage_agent_sdk.runtime import _build_agent_card, _SdkAgentExecutor

        card = _build_agent_card(8999)
        executor = _SdkAgentExecutor(
            hooks=_make_hooks(),
            concurrent_threads=True,
            initialize_done=asyncio.Event(),
        )
        handler = DefaultRequestHandler(
            agent_executor=executor,
            task_store=InMemoryTaskStore(),
            agent_card=card,
            queue_manager=InMemoryQueueManager(),
        )
        # Mirror the live composition in AgentRuntime._serve.
        routes = (
            create_agent_card_routes(card)
            + create_agent_card_routes(card, card_url="/.well-known/agent.json")
            + create_rest_routes(handler)
        )
        client = TestClient(Starlette(routes=routes))

        canonical = client.get("/.well-known/agent-card.json")
        assert canonical.status_code == 200, (
            f"SDK canonical agent-card path 404'd — likely the /{{tenant}} mount is "
            f"swallowing it. Body: {canonical.text!r}"
        )
        legacy = client.get("/.well-known/agent.json")
        assert legacy.status_code == 200, (
            f"Legacy agent.json alias 404'd — smoke contract broken. Body: {legacy.text!r}"
        )


class TestStructuredEnvelopeDataPart:
    """ADR-0066 §3: the platform delivers the structured inbound envelope as a
    dedicated A2A DataPart. ``_build_message_from_a2a`` reads it as data and
    only falls back to parsing the rendered prose when no DataPart is present.
    """

    @staticmethod
    def _text_part(text: str):
        from a2a.types.a2a_pb2 import Part

        p = Part()
        p.text = text
        return p

    @staticmethod
    def _data_part(data: dict):
        from a2a.types.a2a_pb2 import Part
        from google.protobuf.json_format import ParseDict

        p = Part()
        # Mirrors a2a's v0.3->core ingestion (compat/v0_3/conversions.py): an
        # incoming data Part's payload lands in part.data.struct_value.
        ParseDict(data, p.data.struct_value)
        return p

    def _ctx(self, parts):
        message = SimpleNamespace(role="user", parts=parts, metadata=None)
        ctx = MagicMock()
        ctx.task_id = "task-1"
        ctx.context_id = "thread-1"
        ctx.message = message
        return ctx

    def test_prefers_data_part_over_prose(self):
        from spring_voyage_agent_sdk.runtime import _build_message_from_a2a

        envelope_data = {
            "envelopes": [
                {
                    "from": "agent:writer",
                    "to": ["agent:editor"],
                    "participants": ["agent:writer", "agent:editor"],
                    "message_id": "msg-from-data",
                    "in_reply_to": "brief-7",
                }
            ]
        }
        # The prose TextPart carries a DIFFERENT envelope; the structured
        # DataPart must win so the engine never re-parses prose.
        prose = '```json\n{"from": "agent:other", "message_id": "msg-from-prose", "to": [], "participants": []}\n```'
        ctx = self._ctx([self._text_part(prose), self._data_part(envelope_data)])

        message = _build_message_from_a2a(ctx)

        assert message.envelope is not None
        assert message.envelope.from_address == "agent:writer"
        assert message.envelope.message_id == "msg-from-data"
        assert message.envelope.in_reply_to == "brief-7"

    def test_falls_back_to_prose_when_no_data_part(self):
        from spring_voyage_agent_sdk.runtime import _build_message_from_a2a

        prose = '```json\n{"from": "agent:writer", "message_id": "msg-from-prose", "to": [], "participants": []}\n```'
        ctx = self._ctx([self._text_part(prose)])

        message = _build_message_from_a2a(ctx)

        assert message.envelope is not None
        assert message.envelope.message_id == "msg-from-prose"

    def test_extractor_ignores_text_only_parts(self):
        from spring_voyage_agent_sdk.runtime import _extract_envelope_data_from_parts

        assert _extract_envelope_data_from_parts([self._text_part("no data here")]) is None

    def test_extractor_decodes_envelopes_payload(self):
        from spring_voyage_agent_sdk.runtime import _extract_envelope_data_from_parts

        data = {"envelopes": [{"from": "agent:x", "message_id": "m1"}]}
        decoded = _extract_envelope_data_from_parts([self._data_part(data)])
        assert decoded is not None
        assert decoded["envelopes"][0]["message_id"] == "m1"
