"""Tests for agent.py — DaprAgentExecutor and the plain-Python agentic loop.

Issue #1199 / ADR 0029 Stage 0: the agent dropped the
``dapr_agents.DurableAgent`` workflow wrapper. The previous test surface
(``_build_agent``, ``_build_agent_kwargs``, ``runner.run``) is gone with
it. These tests cover the replacement: a thin LLM + tool-call loop that
talks to Dapr Conversation directly and uses MCP tools synchronously,
without involving the Dapr workflow runtime.
"""

from __future__ import annotations

from types import SimpleNamespace
from typing import Awaitable, Callable
from unittest.mock import AsyncMock, MagicMock, patch

import pytest
from dapr_agents.types.message import (
    AssistantMessage,
    FunctionCall,
    LLMChatCandidate,
    LLMChatResponse,
    ToolCall,
)

from agent import (
    AgentBuild,
    DaprAgentExecutor,
    _build_agent_runtime,
    _run_agentic_loop,
)


def _text_part(text: str) -> SimpleNamespace:
    """Mimic the a2a-sdk v0.3+ ``Part`` shape: a discriminated-union wrapper
    around ``TextPart | FilePart | DataPart`` exposed via ``part.root``.

    Reading ``part.text`` directly raises ``AttributeError`` against the real
    SDK, which the JSON-RPC layer surfaces as a -32603 internal error. Tests
    that pass through ``DaprAgentExecutor.execute`` must therefore mirror the
    discriminated-root shape so we exercise the same access path the SDK
    actually delivers.
    """
    return SimpleNamespace(root=SimpleNamespace(kind="text", text=text))


def _non_text_part() -> SimpleNamespace:
    """Mimic a non-text ``Part`` (file/data) — root has no ``.text``."""
    return SimpleNamespace(root=SimpleNamespace(kind="file", file=object()))


def _final_response(text: str) -> LLMChatResponse:
    """Build an LLMChatResponse with a single assistant message and no tool
    calls — i.e. a final answer that should terminate the agent loop."""
    return LLMChatResponse(
        results=[
            LLMChatCandidate(
                message=AssistantMessage(content=text),
                finish_reason="stop",
            ),
        ]
    )


def _tool_call_response(name: str, arguments_json: str) -> LLMChatResponse:
    """Build an LLMChatResponse where the assistant requests a tool call.

    Uses ``model_construct`` to skip pydantic validation on
    ``FunctionCall.arguments`` so tests can exercise the loop's
    JSON-decode error path with a deliberately malformed string. (The
    upstream validator rejects invalid JSON at construction time, but
    the loop still has defensive json-parse handling because nothing
    enforces well-formed arguments end-to-end at runtime — e.g. a
    custom Conversation backend could feed the model arbitrary text.)
    """
    function_call = FunctionCall.model_construct(
        name=name,
        arguments=arguments_json,
    )
    tool_call = ToolCall.model_construct(
        id="call-1",
        type="function",
        function=function_call,
    )
    return LLMChatResponse(
        results=[
            LLMChatCandidate(
                message=AssistantMessage(content="", tool_calls=[tool_call]),
                finish_reason="tool_calls",
            ),
        ]
    )


class TestDaprAgentExecutor:
    @staticmethod
    def _make_factory(
        build: AgentBuild,
    ) -> "Callable[[], Awaitable[AgentBuild]]":
        async def factory():
            return build

        return factory

    @staticmethod
    def _build_with_llm(llm: MagicMock) -> AgentBuild:
        return AgentBuild(
            llm=llm,
            tools=[],
            system_prompt="You are a helpful AI assistant.",
            tools_by_name={},
        )

    @pytest.mark.asyncio
    async def test_execute_enqueues_completed_status(self):
        """Happy path: LLM returns a final answer, executor enqueues a
        completed task with the answer as the artifact."""
        llm = MagicMock()
        llm.generate = MagicMock(return_value=_final_response("Hello from agent!"))

        executor = DaprAgentExecutor(
            self._make_factory(self._build_with_llm(llm)),
        )

        context = MagicMock()
        # Provide a truthy current_task so new_task() is not called.
        context.current_task = MagicMock()
        context.task_id = "task-1"
        context.context_id = "ctx-1"
        context.message = MagicMock()
        context.message.parts = [_text_part("What is 2+2?")]

        event_queue = MagicMock()
        event_queue.enqueue_event = AsyncMock()

        await executor.execute(context, event_queue)

        # Should have enqueued: task, working status, artifact, completed status
        assert event_queue.enqueue_event.call_count == 4
        # Verify the LLM was called once with the user message. The loop
        # mutates the messages list in-place after each LLM round (appends
        # the assistant reply), and MagicMock holds a reference to that
        # same list — so by the time we inspect call_args, an
        # AssistantMessage sits at the tail. Look up the user message by
        # role rather than by position.
        llm.generate.assert_called_once()
        messages = llm.generate.call_args.kwargs["messages"]
        user_messages = [m for m in messages if getattr(m, "role", None) == "user"]
        assert user_messages
        assert user_messages[-1].content == "What is 2+2?"

    @pytest.mark.asyncio
    async def test_execute_handles_agent_error(self):
        llm = MagicMock()
        llm.generate = MagicMock(side_effect=RuntimeError("LLM unreachable"))

        executor = DaprAgentExecutor(
            self._make_factory(self._build_with_llm(llm)),
        )

        context = MagicMock()
        context.current_task = MagicMock()
        context.task_id = "task-2"
        context.context_id = "ctx-2"
        context.message = MagicMock()
        context.message.parts = []

        event_queue = MagicMock()
        event_queue.enqueue_event = AsyncMock()

        await executor.execute(context, event_queue)

        # Should have enqueued: task, working status, failed status
        assert event_queue.enqueue_event.call_count == 3

    @pytest.mark.asyncio
    async def test_execute_extracts_text_via_discriminated_root(self):
        """Regression: a2a-sdk v0.3+ wraps each part in a ``Part(root=...)``
        discriminated union. The executor must read text via ``part.root.text``
        and skip non-text parts; reading ``part.text`` directly raises
        AttributeError and crashes the JSON-RPC handler with -32603.
        """
        llm = MagicMock()
        llm.generate = MagicMock(return_value=_final_response("ok"))

        executor = DaprAgentExecutor(
            self._make_factory(self._build_with_llm(llm)),
        )

        context = MagicMock()
        context.current_task = MagicMock()
        context.task_id = "task-parts"
        context.context_id = "ctx-parts"
        context.message = MagicMock()
        # Mix a non-text part in between two text parts to confirm the
        # executor concatenates text parts and silently skips others rather
        # than throwing on the missing attribute.
        context.message.parts = [
            _text_part("hello "),
            _non_text_part(),
            _text_part("world"),
        ]

        event_queue = MagicMock()
        event_queue.enqueue_event = AsyncMock()

        await executor.execute(context, event_queue)

        llm.generate.assert_called_once()
        messages = llm.generate.call_args.kwargs["messages"]
        # Look up the user message by role — see comment in
        # test_execute_enqueues_completed_status for why position-based
        # indexing is unreliable here.
        user_messages = [m for m in messages if getattr(m, "role", None) == "user"]
        assert user_messages
        assert user_messages[-1].content == "hello world"

    @pytest.mark.asyncio
    async def test_cancel_enqueues_canceled_status(self):
        llm = MagicMock()
        executor = DaprAgentExecutor(
            self._make_factory(self._build_with_llm(llm)),
        )

        context = MagicMock()
        context.task_id = "task-3"
        context.context_id = "ctx-3"

        event_queue = MagicMock()
        event_queue.enqueue_event = AsyncMock()

        await executor.cancel(context, event_queue)

        assert event_queue.enqueue_event.call_count == 1

    @pytest.mark.asyncio
    async def test_factory_is_called_once_across_invocations(self):
        """Lazy build cache: the factory runs at most once per executor."""
        llm = MagicMock()
        llm.generate = MagicMock(return_value=_final_response("ok"))

        call_count = {"n": 0}

        async def counting_factory():
            call_count["n"] += 1
            return self._build_with_llm(llm)

        executor = DaprAgentExecutor(counting_factory)

        for _ in range(3):
            context = MagicMock()
            context.current_task = MagicMock()
            context.task_id = "t"
            context.context_id = "c"
            context.message = MagicMock()
            context.message.parts = []
            event_queue = MagicMock()
            event_queue.enqueue_event = AsyncMock()
            await executor.execute(context, event_queue)

        assert call_count["n"] == 1
        assert llm.generate.call_count == 3


class TestAgenticLoop:
    """Behaviour of :func:`_run_agentic_loop` — the workflow-free
    tool-calling loop introduced in #1199 / ADR 0029 Stage 0."""

    @staticmethod
    def _build(llm: MagicMock, tools=None, tools_by_name=None) -> AgentBuild:
        return AgentBuild(
            llm=llm,
            tools=tools or [],
            system_prompt="You are a helpful AI assistant.",
            tools_by_name=tools_by_name or {},
        )

    @pytest.mark.asyncio
    async def test_returns_assistant_text_when_no_tool_calls(self):
        llm = MagicMock()
        llm.generate = MagicMock(return_value=_final_response("answer"))

        result = await _run_agentic_loop(self._build(llm), "ping")

        assert result == "answer"
        llm.generate.assert_called_once()

    @pytest.mark.asyncio
    async def test_invokes_tool_then_returns_final_answer(self):
        """One round of tool call → one round of final answer."""
        llm = MagicMock()
        llm.generate = MagicMock(
            side_effect=[
                _tool_call_response("echo", '{"text": "hello"}'),
                _final_response("the tool said hello"),
            ]
        )

        tool = MagicMock()
        tool.name = "echo"
        tool.arun = AsyncMock(return_value="hello")

        build = self._build(
            llm,
            tools=[tool],
            tools_by_name={"echo": tool},
        )

        result = await _run_agentic_loop(build, "say hi")

        assert result == "the tool said hello"
        # Tool was called with the arguments the LLM emitted.
        tool.arun.assert_awaited_once_with(text="hello")
        # Two LLM rounds: the tool-calling one and the final answer one.
        assert llm.generate.call_count == 2

    @pytest.mark.asyncio
    async def test_unknown_tool_is_reported_back_to_llm(self):
        """The loop never aborts on unknown tools — it surfaces the
        failure as a ToolMessage so the model can recover."""
        llm = MagicMock()
        llm.generate = MagicMock(
            side_effect=[
                _tool_call_response("ghost", "{}"),
                _final_response("sorry, no such tool"),
            ]
        )

        result = await _run_agentic_loop(self._build(llm), "do a thing")

        assert result == "sorry, no such tool"
        # Second LLM call sees the assistant's tool_call AND the
        # ToolMessage with the failure text.
        second_messages = llm.generate.call_args_list[1].kwargs["messages"]
        tool_messages = [m for m in second_messages if getattr(m, "role", None) == "tool"]
        assert tool_messages
        assert "not registered" in tool_messages[-1].content

    @pytest.mark.asyncio
    async def test_invalid_tool_arguments_surface_as_tool_message(self):
        """Malformed JSON arguments must not crash the loop — the model
        should see the parse error and re-plan."""
        llm = MagicMock()
        llm.generate = MagicMock(
            side_effect=[
                _tool_call_response("echo", "{not valid json"),
                _final_response("recovered"),
            ]
        )

        tool = MagicMock()
        tool.name = "echo"
        tool.arun = AsyncMock(return_value="never called")

        build = self._build(
            llm,
            tools=[tool],
            tools_by_name={"echo": tool},
        )

        result = await _run_agentic_loop(build, "do a thing")

        assert result == "recovered"
        tool.arun.assert_not_awaited()

    @pytest.mark.asyncio
    async def test_tool_exception_becomes_tool_message(self):
        """A raising tool surfaces its error to the model rather than
        bubbling up out of the loop."""
        llm = MagicMock()
        llm.generate = MagicMock(
            side_effect=[
                _tool_call_response("boom", "{}"),
                _final_response("noted"),
            ]
        )

        tool = MagicMock()
        tool.name = "boom"
        tool.arun = AsyncMock(side_effect=RuntimeError("tool exploded"))

        build = self._build(
            llm,
            tools=[tool],
            tools_by_name={"boom": tool},
        )

        result = await _run_agentic_loop(build, "go")

        assert result == "noted"
        # The error text is in the tool message the model saw on round 2.
        second_messages = llm.generate.call_args_list[1].kwargs["messages"]
        tool_messages = [m for m in second_messages if getattr(m, "role", None) == "tool"]
        assert tool_messages
        assert "tool exploded" in tool_messages[-1].content

    @pytest.mark.asyncio
    async def test_step_budget_caps_loop(self, monkeypatch):
        """If the model keeps requesting tools forever, the loop must
        terminate after SPRING_AGENT_MAX_STEPS rounds rather than spin."""
        monkeypatch.setenv("SPRING_AGENT_MAX_STEPS", "2")

        llm = MagicMock()
        # Always ask for a tool — never converge on a final answer.
        llm.generate = MagicMock(
            return_value=_tool_call_response("echo", '{"text": "again"}'),
        )

        tool = MagicMock()
        tool.name = "echo"
        tool.arun = AsyncMock(return_value="again")

        build = self._build(
            llm,
            tools=[tool],
            tools_by_name={"echo": tool},
        )

        result = await _run_agentic_loop(build, "loop forever")

        # Loop ran exactly the budget.
        assert llm.generate.call_count == 2
        # Result is a sentinel rather than an exception.
        assert "no final response" in result


class TestBuildAgentRuntime:
    @pytest.mark.asyncio
    async def test_builds_runtime_without_mcp(self, monkeypatch):
        monkeypatch.delenv("SPRING_MCP_ENDPOINT", raising=False)
        monkeypatch.delenv("SPRING_AGENT_TOKEN", raising=False)

        with patch("agent.DaprChatClient") as mock_client_cls:
            mock_client_cls.return_value = MagicMock()
            build = await _build_agent_runtime()

        assert build.tools == []
        assert build.system_prompt == "You are a helpful AI assistant."
        # Component name defaults to llm-provider.
        mock_client_cls.assert_called_once_with(component_name="llm-provider")

    @pytest.mark.asyncio
    async def test_builds_runtime_with_custom_prompt(self, monkeypatch):
        monkeypatch.delenv("SPRING_MCP_ENDPOINT", raising=False)
        monkeypatch.delenv("SPRING_AGENT_TOKEN", raising=False)
        monkeypatch.setenv("SPRING_SYSTEM_PROMPT", "Be concise.")

        with patch("agent.DaprChatClient") as mock_client_cls:
            mock_client_cls.return_value = MagicMock()
            build = await _build_agent_runtime()

        assert build.system_prompt == "Be concise."

    @pytest.mark.asyncio
    async def test_respects_llm_component_override(self, monkeypatch):
        monkeypatch.delenv("SPRING_MCP_ENDPOINT", raising=False)
        monkeypatch.delenv("SPRING_AGENT_TOKEN", raising=False)
        monkeypatch.setenv("SPRING_LLM_COMPONENT", "custom-llm")

        with patch("agent.DaprChatClient") as mock_client_cls:
            mock_client_cls.return_value = MagicMock()
            await _build_agent_runtime()

        mock_client_cls.assert_called_once_with(component_name="custom-llm")

    @pytest.mark.asyncio
    async def test_builds_runtime_with_mcp_tools(self, monkeypatch):
        monkeypatch.setenv("SPRING_MCP_ENDPOINT", "http://mcp:9999/mcp")
        monkeypatch.setenv("SPRING_AGENT_TOKEN", "tok-abc")

        mock_tool_defs = [
            {
                "name": "list-files",
                "description": "List files",
                "inputSchema": {
                    "type": "object",
                    "properties": {"dir": {"type": "string"}},
                    "required": ["dir"],
                },
            }
        ]

        with (
            patch("agent.discover_tools", new_callable=AsyncMock) as mock_discover,
            patch("agent.create_tool_proxy") as mock_create_proxy,
            patch("agent.DaprChatClient") as mock_client_cls,
        ):
            mock_discover.return_value = mock_tool_defs
            proxy = MagicMock()
            proxy.name = "list-files"
            mock_create_proxy.return_value = proxy
            mock_client_cls.return_value = MagicMock()

            build = await _build_agent_runtime()

        assert len(build.tools) == 1
        assert build.tools_by_name["list-files"] is proxy
