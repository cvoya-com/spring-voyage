"""Tests for agent.py — DaprAgentExecutor and agent build logic."""

from __future__ import annotations

from unittest.mock import AsyncMock, MagicMock, patch

import pytest

from agent import DaprAgentExecutor, _build_agent


class TestDaprAgentExecutor:
    @pytest.mark.asyncio
    async def test_execute_enqueues_completed_status(self):
        mock_agent = MagicMock()
        mock_agent.run = AsyncMock(return_value="Hello from agent!")

        executor = DaprAgentExecutor(mock_agent)

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

    @pytest.mark.asyncio
    async def test_execute_handles_agent_error(self):
        mock_agent = MagicMock()
        mock_agent.run = AsyncMock(side_effect=RuntimeError("LLM unreachable"))

        executor = DaprAgentExecutor(mock_agent)

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
        executor = DaprAgentExecutor(mock_agent)

        context = MagicMock()
        context.task_id = "task-3"
        context.context_id = "ctx-3"

        event_queue = MagicMock()
        event_queue.enqueue_event = AsyncMock()

        await executor.cancel(context, event_queue)

        assert event_queue.enqueue_event.call_count == 1


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
    async def test_passes_provider_and_model_to_agent(self, monkeypatch):
        """SPRING_LLM_PROVIDER and SPRING_MODEL must flow into the DurableAgent
        constructor as `llm` and `model` so the Dapr Conversation component is
        pinned explicitly — not silently resolved by the SDK default."""
        monkeypatch.delenv("SPRING_MCP_ENDPOINT", raising=False)
        monkeypatch.delenv("SPRING_AGENT_TOKEN", raising=False)
        monkeypatch.setenv("SPRING_LLM_PROVIDER", "openai")
        monkeypatch.setenv("SPRING_MODEL", "gpt-4o-mini")

        with patch("agent.Agent") as mock_agent_cls:
            mock_agent_cls.return_value = MagicMock()
            await _build_agent()

        call_kwargs = mock_agent_cls.call_args[1]
        assert call_kwargs["llm"] == "openai"
        assert call_kwargs["model"] == "gpt-4o-mini"

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
