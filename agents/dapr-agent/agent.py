#!/usr/bin/env python3
"""
Spring Voyage Dapr Agent — main entrypoint.

A platform-managed agentic loop that:
  1. Discovers tools from the Spring Voyage MCP server.
  2. Runs a plain-Python tool-calling loop against Dapr Conversation
     (Ollama by default, any Conversation-compatible provider).
  3. Exposes the result via an A2A endpoint.

Why no Dapr Workflow / DurableAgent: ADR 0029 Stage 0 (issue #1199
follow-up). An ephemeral agent is defined by container-lifetime-tied-to-
completion-of-work, not by any specific durability mechanism. Going
through `dapr_agents.DurableAgent` requires the Dapr workflow runtime
(actor placement + scheduler) — those services live on `spring-net` per
ADR 0028, but the per-launch daprd sidecar joins the per-workflow bridge
and the tenant network only, so workflow start consistently times out
with `DaprBuiltInActorNotFoundRetries` retries on the sidecar. The
agentic loop is implemented inline below using `DaprChatClient` + the
MCP tool proxies; durability of in-flight state is the workspace volume
ADR 0029 § "Durable state" defines, not the workflow runtime.

Configuration via environment variables:
  SPRING_MCP_ENDPOINT   — URL of the platform MCP server (required)
  SPRING_AGENT_TOKEN    — Bearer token for MCP authentication (required)
  SPRING_MODEL          — LLM model name (default: llama3.2:3b). The
                          Dapr Conversation component reads the model
                          from its own metadata (deployed
                          conversation-*.yaml); SPRING_MODEL is kept
                          here for telemetry / agent-card rendering.
  SPRING_LLM_PROVIDER   — Provider type label for telemetry / agent
                          card description (e.g. ``ollama``, ``openai``).
                          The actual Dapr Conversation component name is
                          ``llm-provider`` by convention (overridable via
                          SPRING_LLM_COMPONENT).
  SPRING_LLM_COMPONENT  — Optional override for the Dapr Conversation
                          component name (default: ``llm-provider``).
  SPRING_SYSTEM_PROMPT  — System prompt assembled by the platform (optional)
  AGENT_PORT            — A2A server listen port (default: 8999)
  SPRING_AGENT_MAX_STEPS — Maximum tool-call rounds before forcing the
                          loop to terminate (default: 12). Guards
                          against runaway loops without imposing a wall
                          clock that would fight the upstream LLM
                          timeout.
"""

from __future__ import annotations

import asyncio
import json
import logging
import os
from dataclasses import dataclass
from typing import Awaitable, Callable, List

import uvicorn
from a2a.server.agent_execution import AgentExecutor, RequestContext
from a2a.server.events import EventQueue
from a2a.types import (
    TaskArtifactUpdateEvent,
    TaskState,
    TaskStatus,
    TaskStatusUpdateEvent,
)
from a2a.utils.artifact import new_text_artifact
from a2a.utils.message import new_agent_text_message
from a2a.utils.task import new_task
from dapr_agents.llm import DaprChatClient
from dapr_agents.tool.base import AgentTool
from dapr_agents.types.message import (
    AssistantMessage,
    BaseMessage,
    SystemMessage,
    ToolMessage,
    UserMessage,
)

from a2a_server import DEFAULT_PORT, create_a2a_app
from mcp_bridge import create_tool_proxy, discover_tools

logging.basicConfig(
    level=logging.INFO,
    format="%(asctime)s %(levelname)s [%(name)s] %(message)s",
)
logger = logging.getLogger("dapr-agent")

DEFAULT_LLM_COMPONENT = "llm-provider"
DEFAULT_MAX_STEPS = 12


@dataclass
class AgentBuild:
    """Cached agent runtime — the LLM client + the resolved tool list."""

    llm: DaprChatClient
    tools: List[AgentTool]
    system_prompt: str
    tools_by_name: dict[str, AgentTool]


AgentFactory = Callable[[], Awaitable[AgentBuild]]


class DaprAgentExecutor(AgentExecutor):
    """A2A executor that runs a plain-Python tool-calling loop.

    Construction is *lazy*: the executor is handed an async factory that
    builds the LLM client + tool list on first invocation. This keeps the
    A2A server boot fast (the agent card is purely static — see
    :func:`a2a_server.build_agent_card`) and makes the ``GET
    /.well-known/agent-card.json`` smoke probe succeed immediately even
    when no Dapr sidecar is reachable. The first ``message/send`` pays
    the one-off construction cost; subsequent ones reuse the cache.
    """

    def __init__(
        self,
        factory: "AgentFactory",
    ) -> None:
        self._factory = factory
        self._build: AgentBuild | None = None
        self._lock = asyncio.Lock()

    async def _ensure_built(self) -> AgentBuild:
        if self._build is not None:
            return self._build
        async with self._lock:
            if self._build is None:
                self._build = await self._factory()
            return self._build

    async def execute(
        self,
        context: RequestContext,
        event_queue: EventQueue,
    ) -> None:
        """Run the agentic loop for a single A2A task."""
        task = context.current_task or new_task(context.message)
        await event_queue.enqueue_event(task)

        # Extract user text from the incoming A2A message.
        # In a2a-sdk v0.3+ `Part` is a discriminated-union wrapper around
        # `TextPart | FilePart | DataPart` exposed via `part.root`; only
        # `TextPart` (kind == "text") carries a `.text` attribute. Reading
        # `part.text` directly raises AttributeError, which the SDK then
        # surfaces as a JSON-RPC -32603 (Internal Error). Pull the text via
        # the discriminated root and skip non-text parts.
        user_text = ""
        if context.message and context.message.parts:
            for part in context.message.parts:
                root = getattr(part, "root", part)
                text = getattr(root, "text", None)
                if text:
                    user_text += text

        await event_queue.enqueue_event(
            TaskStatusUpdateEvent(
                task_id=context.task_id,
                context_id=context.context_id,
                final=False,
                status=TaskStatus(
                    state=TaskState.working,
                    message=new_agent_text_message("Running agentic loop..."),
                ),
            )
        )

        try:
            build = await self._ensure_built()
            result_text = await _run_agentic_loop(build, user_text)

            await event_queue.enqueue_event(
                TaskArtifactUpdateEvent(
                    task_id=context.task_id,
                    context_id=context.context_id,
                    artifact=new_text_artifact(name="result", text=result_text),
                )
            )
            await event_queue.enqueue_event(
                TaskStatusUpdateEvent(
                    task_id=context.task_id,
                    context_id=context.context_id,
                    final=True,
                    status=TaskStatus(
                        state=TaskState.completed,
                    ),
                )
            )
        except Exception as exc:
            logger.exception("Agent loop failed")
            await event_queue.enqueue_event(
                TaskStatusUpdateEvent(
                    task_id=context.task_id,
                    context_id=context.context_id,
                    final=True,
                    status=TaskStatus(
                        state=TaskState.failed,
                        message=new_agent_text_message(f"Error: {exc}"),
                    ),
                )
            )

    async def cancel(
        self,
        context: RequestContext,
        event_queue: EventQueue,
    ) -> None:
        """Cancel a running task."""
        await event_queue.enqueue_event(
            TaskStatusUpdateEvent(
                task_id=context.task_id,
                context_id=context.context_id,
                final=True,
                status=TaskStatus(
                    state=TaskState.canceled,
                    message=new_agent_text_message("Task canceled."),
                ),
            )
        )


async def _run_agentic_loop(build: AgentBuild, user_text: str) -> str:
    """Run a tool-calling loop until the LLM returns a final assistant
    message (no further tool calls) or ``SPRING_AGENT_MAX_STEPS`` rounds
    elapse. Returns the assistant's final textual response.

    Each iteration is one Conversation API call to daprd (a single unary
    gRPC, no workflow / actor placement involvement) plus, if the model
    asked for tools, one HTTP call per tool invocation against the
    Spring MCP server.
    """
    max_steps = int(os.environ.get("SPRING_AGENT_MAX_STEPS", str(DEFAULT_MAX_STEPS)))

    messages: List[BaseMessage] = []
    if build.system_prompt:
        messages.append(SystemMessage(content=build.system_prompt))
    messages.append(UserMessage(content=user_text))

    for step in range(max_steps):
        logger.info(
            "Agent loop step %d/%d (messages=%d, tools=%d)",
            step + 1,
            max_steps,
            len(messages),
            len(build.tools),
        )
        response = build.llm.generate(messages=messages, tools=build.tools)
        assistant = response.get_message()
        if assistant is None:
            raise RuntimeError(
                "LLM returned no candidate message — check Conversation component health.",
            )

        messages.append(assistant)

        tool_calls = getattr(assistant, "tool_calls", None) or []
        if not tool_calls:
            return assistant.content or ""

        # Run the requested tools sequentially. Errors per-tool are
        # surfaced back to the model so it can re-plan or apologise; we
        # never abort the whole turn on a single tool failure.
        for tool_call in tool_calls:
            tool_name = tool_call.function.name
            try:
                tool_args = json.loads(tool_call.function.arguments or "{}")
            except json.JSONDecodeError as exc:
                tool_args = {}
                tool_result_text = f"Failed to decode tool arguments JSON for '{tool_name}': {exc}"
                logger.warning(tool_result_text)
                messages.append(
                    ToolMessage(
                        content=tool_result_text,
                        tool_call_id=tool_call.id,
                    )
                )
                continue

            tool = build.tools_by_name.get(tool_name)
            if tool is None:
                tool_result_text = f"Tool '{tool_name}' is not registered with this agent."
                logger.warning(tool_result_text)
                messages.append(
                    ToolMessage(
                        content=tool_result_text,
                        tool_call_id=tool_call.id,
                    )
                )
                continue

            try:
                tool_result = await tool.arun(**tool_args)
            except Exception as exc:
                logger.exception("Tool %s failed", tool_name)
                tool_result = f"Error invoking tool '{tool_name}': {exc}"

            messages.append(
                ToolMessage(
                    content=str(tool_result) if tool_result is not None else "",
                    tool_call_id=tool_call.id,
                )
            )

    # Loop budget exhausted without a final assistant message — return
    # whatever the last assistant message said so the caller has *something*
    # rather than a blank artifact.
    last_assistant = next(
        (m for m in reversed(messages) if isinstance(m, AssistantMessage)),
        None,
    )
    return (last_assistant.content if last_assistant else "") or (
        f"(no final response after {max_steps} agent loop steps)"
    )


def _build_system_prompt() -> str:
    """Resolve the system prompt fed into the loop's first turn."""
    return os.environ.get(
        "SPRING_SYSTEM_PROMPT",
        "You are a helpful AI assistant.",
    )


def _resolve_tool_name(tool: AgentTool, fallback: str) -> str:
    """Pick the canonical name the LLM tool-call will reference.

    AgentTool exposes a ``name`` attribute on the instance (set by the
    constructor); fall back to a stable string if it's missing on a
    third-party subclass so the by-name lookup in
    :func:`_run_agentic_loop` doesn't silently key on ``None``.
    """
    return getattr(tool, "name", None) or fallback


async def _build_agent_runtime() -> AgentBuild:
    """Discover MCP tools and assemble the LLM client + tool index."""
    mcp_endpoint = os.environ.get("SPRING_MCP_ENDPOINT", "")
    mcp_token = os.environ.get("SPRING_AGENT_TOKEN", "")
    model = os.environ.get("SPRING_MODEL", "llama3.2:3b")
    provider = os.environ.get("SPRING_LLM_PROVIDER", "ollama")
    component_name = os.environ.get("SPRING_LLM_COMPONENT", DEFAULT_LLM_COMPONENT)

    llm_client = DaprChatClient(component_name=component_name)
    tools: list[AgentTool] = []

    if mcp_endpoint and mcp_token:
        try:
            tool_defs = await discover_tools(mcp_endpoint, mcp_token)
            for td in tool_defs:
                proxy = create_tool_proxy(td, mcp_endpoint, mcp_token)
                tools.append(proxy)
            logger.info("Loaded %d MCP tool proxies", len(tools))
        except Exception:
            logger.exception(
                "Failed to discover MCP tools; continuing without tools",
            )
    else:
        logger.warning(
            "SPRING_MCP_ENDPOINT or SPRING_AGENT_TOKEN not set; running without MCP tools",
        )

    tools_by_name = {_resolve_tool_name(t, f"tool-{i}"): t for i, t in enumerate(tools)}

    logger.info(
        "Dapr Agent built (workflow-free per ADR 0029 Stage 0): provider=%s, model=%s, component=%s, tools=%d",
        provider,
        model,
        component_name,
        len(tools),
    )

    return AgentBuild(
        llm=llm_client,
        tools=tools,
        system_prompt=_build_system_prompt(),
        tools_by_name=tools_by_name,
    )


def main() -> None:
    """Start the Dapr Agent with the A2A server.

    The A2A application is mounted with a *lazy* executor: the LLM client
    and tool index are only constructed on the first ``message/send``
    call. This lets ``GET /.well-known/agent-card.json`` answer
    immediately even when no Dapr sidecar is reachable (the boot-time
    smoke contract), and keeps tool-discovery cost off the critical
    path of container startup.
    """
    port = int(os.environ.get("AGENT_PORT", str(DEFAULT_PORT)))

    executor = DaprAgentExecutor(_build_agent_runtime)
    a2a_app = create_a2a_app(executor, port=port)

    logger.info("Starting Dapr Agent A2A server on port %d", port)
    uvicorn.run(a2a_app.build(), host="0.0.0.0", port=port)


if __name__ == "__main__":
    main()
