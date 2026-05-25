"""Tests for agent.py — SDK-migrated Dapr Agent agentic loop and hooks.

The agent was migrated onto the Spring Voyage Agent SDK (issue #1272).
The A2A executor surface (DaprAgentExecutor) is now inside the SDK;
agent.py exposes the three lifecycle hooks (initialize / on_message /
on_shutdown) and the internal _run_agentic_loop helper.

These tests cover:
  - _run_agentic_loop: the plain-Python tool-calling loop (unchanged).
  - initialize(): wires up the LLM client and tool index.
  - on_message(): calls _run_agentic_loop and yields a Response.
"""

from __future__ import annotations

import io
import urllib.error
from unittest.mock import AsyncMock, MagicMock, patch

import pytest
from dapr_agents.types.message import (
    AssistantMessage,
    FunctionCall,
    LLMChatCandidate,
    LLMChatResponse,
    ToolCall,
)
from spring_voyage_agent_sdk import Message
from spring_voyage_agent_sdk.types import Sender

import agent as agent_module
from agent import (
    AgentBuild,
    _ensure_configured_model,
    _ensure_ollama_model_sync,
    _ollama_api_base_url,
    _run_agentic_loop,
    initialize,
    on_message,
)


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
    """Build an LLMChatResponse where the assistant requests a tool call."""
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


def _make_message(text: str = "hello") -> Message:
    return Message(
        thread_id="thr-1",
        message_id="msg-1",
        sender=Sender(kind="human", id="u1"),
        payload={"role": "user", "parts": [{"kind": "text", "text": text}]},
        timestamp="2026-04-28T00:00:00Z",
    )


def _make_context(
    *,
    mcp_url: str = "",
    mcp_token: str = "",
    llm_provider_url: str = "",
    system_prompt: str | None = None,
) -> MagicMock:
    ctx = MagicMock()
    ctx.mcp_url = mcp_url
    ctx.mcp_token = mcp_token
    ctx.llm_provider_url = llm_provider_url
    ctx.system_prompt = system_prompt
    return ctx


class TestAgenticLoop:
    """Behaviour of :func:`_run_agentic_loop`."""

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
        tool.arun.assert_awaited_once_with(text="hello")
        assert llm.generate.call_count == 2

    @pytest.mark.asyncio
    async def test_unknown_tool_is_reported_back_to_llm(self):
        llm = MagicMock()
        llm.generate = MagicMock(
            side_effect=[
                _tool_call_response("ghost", "{}"),
                _final_response("sorry, no such tool"),
            ]
        )

        result = await _run_agentic_loop(self._build(llm), "do a thing")

        assert result == "sorry, no such tool"
        second_messages = llm.generate.call_args_list[1].kwargs["messages"]
        tool_messages = [m for m in second_messages if getattr(m, "role", None) == "tool"]
        assert tool_messages
        assert "not registered" in tool_messages[-1].content

    @pytest.mark.asyncio
    async def test_invalid_tool_arguments_surface_as_tool_message(self):
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
        second_messages = llm.generate.call_args_list[1].kwargs["messages"]
        tool_messages = [m for m in second_messages if getattr(m, "role", None) == "tool"]
        assert tool_messages
        assert "tool exploded" in tool_messages[-1].content

    @pytest.mark.asyncio
    async def test_step_budget_caps_loop(self, monkeypatch):
        # No-silent-swallow contract: when the loop exhausts max-steps
        # without producing a terminal assistant message, we MUST raise
        # a RuntimeError rather than return a placeholder string. The
        # caller (on_message) translates the exception into a structured
        # Response(error=...) so the platform records a failure event
        # instead of completing the task with a fake artifact.
        monkeypatch.setenv("SPRING_AGENT_MAX_STEPS", "2")

        llm = MagicMock()
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

        with pytest.raises(RuntimeError, match="exhausted"):
            await _run_agentic_loop(build, "loop forever")

        assert llm.generate.call_count == 2


class TestInitializeHook:
    @pytest.mark.asyncio
    async def test_builds_runtime_without_mcp(self, monkeypatch):
        monkeypatch.delenv("SPRING_MCP_ENDPOINT", raising=False)
        monkeypatch.delenv("SPRING_AGENT_TOKEN", raising=False)
        # ADR-0038: SPRING_LLM_COMPONENT is required — the launcher always
        # pins `llm-<provider>` and there is no silent default.
        monkeypatch.setenv("SPRING_MODEL", "qwen2.5:7b")
        monkeypatch.setenv("SPRING_LLM_PROVIDER", "ollama")
        monkeypatch.setenv("SPRING_LLM_COMPONENT", "llm-ollama")

        ctx = _make_context(llm_provider_url="http://ollama:11434/v1")

        with patch("agent.DaprChatClient") as mock_client_cls:
            mock_client_cls.return_value = MagicMock()
            await initialize(ctx)

        assert agent_module._agent_build is not None
        assert agent_module._agent_build.tools == []
        assert agent_module._agent_build.provider == "ollama"
        assert agent_module._agent_build.model == "qwen2.5:7b"
        assert agent_module._agent_build.llm_provider_url == "http://ollama:11434/v1"
        # Component name comes from SPRING_LLM_COMPONENT (no default fallback).
        mock_client_cls.assert_called_once_with(component_name="llm-ollama")

    @pytest.mark.asyncio
    async def test_system_prompt_from_context_system_prompt(self, monkeypatch):
        # #2734: the platform-assembled system prompt is delivered via
        # the SDK bootstrap bundle (the launcher writes
        # `.spring/system-prompt.md` and the SDK reads it into
        # context.system_prompt). The legacy SPRING_SYSTEM_PROMPT
        # env-var path was removed at the same time.
        monkeypatch.setenv("SPRING_LLM_COMPONENT", "llm-ollama")

        ctx = _make_context(system_prompt="From .spring/system-prompt.md.")

        with patch("agent.DaprChatClient") as mock_client_cls:
            mock_client_cls.return_value = MagicMock()
            await initialize(ctx)

        assert agent_module._agent_build is not None
        assert agent_module._agent_build.system_prompt == "From .spring/system-prompt.md."

    @pytest.mark.asyncio
    async def test_system_prompt_env_var_is_ignored(self, monkeypatch):
        # #2734: SPRING_SYSTEM_PROMPT is no longer read by agent.py — a
        # leftover value in the env (e.g. a stale operator override or
        # a pre-cutover deployment) must not influence the prompt the
        # loop sees.
        monkeypatch.setenv("SPRING_SYSTEM_PROMPT", "Should be ignored.")
        monkeypatch.setenv("SPRING_LLM_COMPONENT", "llm-ollama")

        ctx = _make_context(system_prompt="From .spring/system-prompt.md.")

        with patch("agent.DaprChatClient") as mock_client_cls:
            mock_client_cls.return_value = MagicMock()
            await initialize(ctx)

        assert agent_module._agent_build is not None
        assert agent_module._agent_build.system_prompt == "From .spring/system-prompt.md."

    @pytest.mark.asyncio
    async def test_system_prompt_default_when_context_has_none(self, monkeypatch):
        # When the bootstrap bundle did not deliver a system prompt
        # (e.g. the smoke-test harness ran without a bundle), the agent
        # falls back to a neutral default so the LLM still has system
        # framing.
        monkeypatch.delenv("SPRING_SYSTEM_PROMPT", raising=False)
        monkeypatch.setenv("SPRING_LLM_COMPONENT", "llm-ollama")

        ctx = _make_context(system_prompt=None)

        with patch("agent.DaprChatClient") as mock_client_cls:
            mock_client_cls.return_value = MagicMock()
            await initialize(ctx)

        assert agent_module._agent_build is not None
        assert agent_module._agent_build.system_prompt == "You are a helpful AI assistant."

    @pytest.mark.asyncio
    async def test_respects_llm_component_override(self, monkeypatch):
        monkeypatch.setenv("SPRING_LLM_COMPONENT", "custom-llm")

        ctx = _make_context()

        with patch("agent.DaprChatClient") as mock_client_cls:
            mock_client_cls.return_value = MagicMock()
            await initialize(ctx)

        mock_client_cls.assert_called_once_with(component_name="custom-llm")

    @pytest.mark.asyncio
    async def test_builds_with_mcp_tools(self, monkeypatch):
        monkeypatch.setenv("SPRING_LLM_COMPONENT", "llm-ollama")
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

        ctx = _make_context(mcp_url="http://mcp:9999/mcp", mcp_token="tok-abc")

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

            await initialize(ctx)

        assert agent_module._agent_build is not None
        assert len(agent_module._agent_build.tools) == 1
        assert agent_module._agent_build.tools_by_name["list-files"] is proxy


class TestOllamaModelReadiness:
    def test_ollama_api_base_url_strips_openai_suffix(self):
        assert _ollama_api_base_url("http://ollama:11434/v1") == "http://ollama:11434"
        assert _ollama_api_base_url("http://ollama:11434/v1/") == "http://ollama:11434"
        assert _ollama_api_base_url("http://ollama:11434") == "http://ollama:11434"

    @pytest.mark.asyncio
    async def test_ensure_configured_model_pulls_ollama_once(self):
        build = AgentBuild(
            llm=MagicMock(),
            tools=[],
            system_prompt="",
            tools_by_name={},
            provider="ollama",
            model="qwen2.5:7b",
            llm_provider_url="http://ollama:11434/v1",
        )

        with patch("agent._ensure_ollama_model", new_callable=AsyncMock) as mock_ensure:
            await _ensure_configured_model(build)
            await _ensure_configured_model(build)

        mock_ensure.assert_awaited_once_with("qwen2.5:7b", "http://ollama:11434/v1")
        assert build.ollama_model_ready is True

    @pytest.mark.asyncio
    async def test_ensure_configured_model_skips_non_ollama_provider(self):
        build = AgentBuild(
            llm=MagicMock(),
            tools=[],
            system_prompt="",
            tools_by_name={},
            provider="openai",
            model="gpt-4o",
            llm_provider_url="https://api.openai.com/v1",
        )

        with patch("agent._ensure_ollama_model", new_callable=AsyncMock) as mock_ensure:
            await _ensure_configured_model(build)

        mock_ensure.assert_not_awaited()
        assert build.ollama_model_ready is False

    def test_missing_ollama_model_triggers_pull(self):
        calls: list[tuple[str, dict]] = []

        def fake_post(_base_url: str, path: str, payload: dict, *, timeout_seconds: int) -> str:
            calls.append((path, payload))
            if path == "/api/show":
                raise urllib.error.HTTPError(
                    url="http://ollama:11434/api/show",
                    code=404,
                    msg="not found",
                    hdrs={},
                    fp=io.BytesIO(b'{"error":"model not found"}'),
                )
            return "{}"

        with patch("agent._post_ollama_json", side_effect=fake_post):
            _ensure_ollama_model_sync("qwen2.5:7b", "http://ollama:11434")

        assert calls == [
            ("/api/show", {"model": "qwen2.5:7b"}),
            ("/api/pull", {"model": "qwen2.5:7b", "stream": False}),
        ]


class TestOnMessageHook:
    @pytest.mark.asyncio
    async def test_yields_response_with_agent_text(self):
        """on_message runs _run_agentic_loop and yields a Response."""
        llm = MagicMock()
        llm.generate = MagicMock(return_value=_final_response("agent says hello"))

        agent_module._agent_build = AgentBuild(
            llm=llm,
            tools=[],
            system_prompt="Be helpful.",
            tools_by_name={},
        )

        message = _make_message("hi")
        chunks = [chunk async for chunk in on_message(message)]

        assert len(chunks) == 1
        assert chunks[0].text == "agent says hello"
        assert chunks[0].final is True
        assert chunks[0].error is None

    @pytest.mark.asyncio
    async def test_yields_error_response_on_loop_failure(self):
        """When _run_agentic_loop raises, on_message yields an error Response."""
        llm = MagicMock()
        llm.generate = MagicMock(side_effect=RuntimeError("LLM down"))

        agent_module._agent_build = AgentBuild(
            llm=llm,
            tools=[],
            system_prompt="Be helpful.",
            tools_by_name={},
        )

        message = _make_message("anything")
        chunks = [chunk async for chunk in on_message(message)]

        assert len(chunks) == 1
        assert chunks[0].error is not None
        assert "LLM down" in chunks[0].error
        assert chunks[0].text is None

    @pytest.mark.asyncio
    async def test_writes_per_thread_turn_counter(self, tmp_path):
        """Demonstrates the ADR-0041 per-thread workspace convention: on_message
        writes a turn counter under ``$SPRING_WORKSPACE_PATH/threads/<thread.id>/``
        via ``IAgentContext.thread_workspace`` (issue #2095)."""
        from spring_voyage_agent_sdk.context import IAgentContext

        llm = MagicMock()
        llm.generate = MagicMock(return_value=_final_response("ok"))

        agent_module._agent_build = AgentBuild(
            llm=llm,
            tools=[],
            system_prompt="Be helpful.",
            tools_by_name={},
        )
        # A real IAgentContext gives us the live thread_workspace helper.
        agent_module._agent_context = IAgentContext(
            tenant_id="t",
            agent_id="a",
            unit_id=None,
            thread_id=None,
            bucket2_url="",
            bucket2_token="",
            llm_provider_url="",
            llm_provider_token="",
            mcp_url="",
            mcp_token="",
            telemetry_url="",
            telemetry_token=None,
            workspace_path=str(tmp_path),
            concurrent_threads=False,
        )
        try:
            message = _make_message("first")
            [_ async for _ in on_message(message)]
            [_ async for _ in on_message(_make_message("second"))]
        finally:
            agent_module._agent_context = None

        counter = tmp_path / "threads" / "thr-1" / "turn-count.txt"
        assert counter.exists()
        assert counter.read_text() == "2"
