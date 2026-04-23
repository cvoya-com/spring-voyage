"""Tests for agent.py — DaprAgentExecutor and agent build logic."""

from __future__ import annotations

import inspect
from typing import Awaitable, Callable
from unittest.mock import AsyncMock, MagicMock, patch

import pytest

from agent import DaprAgentExecutor, _build_agent, _build_agent_kwargs


class TestDaprAgentExecutor:
    @staticmethod
    def _make_factory(
        agent: MagicMock,
        runner: MagicMock,
    ) -> "Callable[[], Awaitable[tuple[MagicMock, MagicMock]]]":
        async def factory():
            return agent, runner

        return factory

    @pytest.mark.asyncio
    async def test_execute_enqueues_completed_status(self):
        mock_agent = MagicMock()
        mock_runner = MagicMock()
        mock_runner.run = AsyncMock(return_value="Hello from agent!")

        executor = DaprAgentExecutor(self._make_factory(mock_agent, mock_runner))

        context = MagicMock()
        # Provide a truthy current_task so new_task() is not called.
        context.current_task = MagicMock()
        context.task_id = "task-1"
        context.context_id = "ctx-1"
        context.message = MagicMock()
        context.message.parts = [MagicMock(text="What is 2+2?")]

        event_queue = MagicMock()
        event_queue.enqueue_event = AsyncMock()

        await executor.execute(context, event_queue)

        # Should have enqueued: task, working status, artifact, completed status
        assert event_queue.enqueue_event.call_count == 4
        # Runner is invoked with the agent + the user text wrapped as a task payload.
        mock_runner.run.assert_awaited_once()
        call_args = mock_runner.run.await_args
        assert call_args.args[0] is mock_agent
        assert call_args.kwargs["payload"] == {"task": "What is 2+2?"}
        assert call_args.kwargs["wait"] is True

    @pytest.mark.asyncio
    async def test_execute_handles_agent_error(self):
        mock_agent = MagicMock()
        mock_runner = MagicMock()
        mock_runner.run = AsyncMock(side_effect=RuntimeError("LLM unreachable"))

        executor = DaprAgentExecutor(self._make_factory(mock_agent, mock_runner))

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
    async def test_cancel_enqueues_canceled_status(self):
        mock_agent = MagicMock()
        mock_runner = MagicMock()
        executor = DaprAgentExecutor(self._make_factory(mock_agent, mock_runner))

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
        mock_agent = MagicMock()
        mock_runner = MagicMock()
        mock_runner.run = AsyncMock(return_value="ok")

        call_count = {"n": 0}

        async def counting_factory():
            call_count["n"] += 1
            return mock_agent, mock_runner

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
        assert mock_runner.run.await_count == 3


class TestBuildAgent:
    @pytest.mark.asyncio
    async def test_builds_agent_without_mcp(self, monkeypatch):
        monkeypatch.delenv("SPRING_MCP_ENDPOINT", raising=False)
        monkeypatch.delenv("SPRING_AGENT_TOKEN", raising=False)

        with patch("agent.Agent") as mock_agent_cls:
            mock_instance = MagicMock()
            mock_instance.name = "SpringDaprAgent"
            mock_agent_cls.return_value = mock_instance

            agent = await _build_agent()

        assert agent is not None
        assert agent.name == "SpringDaprAgent"
        mock_agent_cls.assert_called_once()

    @pytest.mark.asyncio
    async def test_builds_agent_with_custom_prompt(self, monkeypatch):
        monkeypatch.delenv("SPRING_MCP_ENDPOINT", raising=False)
        monkeypatch.delenv("SPRING_AGENT_TOKEN", raising=False)
        monkeypatch.setenv("SPRING_SYSTEM_PROMPT", "Be concise.")

        with patch("agent.Agent") as mock_agent_cls:
            mock_agent_cls.return_value = MagicMock()
            await _build_agent()

        # Verify the Agent constructor was called with the custom prompt.
        call_kwargs = mock_agent_cls.call_args[1]
        assert call_kwargs["instructions"] == ["Be concise."]

    @pytest.mark.asyncio
    async def test_passes_dapr_chat_client_to_agent(self, monkeypatch):
        """Issue #1110: in dapr-agents 1.x ``DurableAgent.__init__`` no
        longer accepts a ``model`` kwarg. The model is configured on the
        Dapr Conversation component (via the deployed conversation-*.yaml
        metadata, which reads ``SPRING_MODEL``) and the agent receives a
        ``DaprChatClient`` instance whose ``component_name`` matches the
        deployed component (``llm-provider`` by default).
        """
        monkeypatch.delenv("SPRING_MCP_ENDPOINT", raising=False)
        monkeypatch.delenv("SPRING_AGENT_TOKEN", raising=False)
        monkeypatch.setenv("SPRING_LLM_PROVIDER", "openai")
        monkeypatch.setenv("SPRING_MODEL", "gpt-4o-mini")

        with patch("agent.Agent") as mock_agent_cls:
            mock_agent_cls.return_value = MagicMock()
            await _build_agent()

        call_kwargs = mock_agent_cls.call_args[1]
        # The legacy `model` kwarg was removed in dapr-agents 1.0.
        assert "model" not in call_kwargs
        # `llm` is now a ChatClientBase instance, not a string identifier.
        llm = call_kwargs["llm"]
        assert getattr(llm, "component_name", None) == "llm-provider"

    @pytest.mark.asyncio
    async def test_respects_llm_component_override(self, monkeypatch):
        monkeypatch.delenv("SPRING_MCP_ENDPOINT", raising=False)
        monkeypatch.delenv("SPRING_AGENT_TOKEN", raising=False)
        monkeypatch.setenv("SPRING_LLM_COMPONENT", "custom-llm")

        with patch("agent.Agent") as mock_agent_cls:
            mock_agent_cls.return_value = MagicMock()
            await _build_agent()

        call_kwargs = mock_agent_cls.call_args[1]
        assert call_kwargs["llm"].component_name == "custom-llm"

    @pytest.mark.asyncio
    async def test_builds_agent_with_mcp_tools(self, monkeypatch):
        monkeypatch.setenv("SPRING_MCP_ENDPOINT", "http://mcp:9999/mcp")
        monkeypatch.setenv("SPRING_AGENT_TOKEN", "tok-abc")

        mock_tools = [
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
            patch("agent.Agent") as mock_agent_cls,
        ):
            mock_discover.return_value = mock_tools
            mock_agent_cls.return_value = MagicMock()
            await _build_agent()

        call_kwargs = mock_agent_cls.call_args[1]
        assert len(call_kwargs["tools"]) == 1


class TestKwargsCompatibility:
    """Regression for issue #1110.

    Validates that the kwargs we hand to ``DurableAgent`` are accepted by
    the real ``DurableAgent.__init__`` signature — catches any future
    upstream rename / removal at unit-test time instead of at container
    boot.
    """

    def test_kwargs_bind_against_real_durable_agent_signature(self):
        from dapr_agents import DurableAgent

        kwargs = _build_agent_kwargs()
        sig = inspect.signature(DurableAgent.__init__)

        try:
            sig.bind_partial(self=None, **kwargs)
        except TypeError as exc:
            pytest.fail(f"_build_agent_kwargs() produced kwargs incompatible with DurableAgent.__init__: {exc}")
