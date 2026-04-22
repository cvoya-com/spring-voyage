#!/usr/bin/env python3
"""
Spring Voyage Dapr Agent — main entrypoint.

A platform-managed agentic loop that:
  1. Discovers tools from the Spring Voyage MCP server.
  2. Runs a Dapr Agents agentic loop against the configured LLM
     (Ollama by default, any Dapr Conversation-compatible provider).
  3. Exposes the result via an A2A endpoint.

Configuration via environment variables:
  SPRING_MCP_ENDPOINT   — URL of the platform MCP server (required)
  SPRING_AGENT_TOKEN    — Bearer token for MCP authentication (required)
  SPRING_MODEL          — LLM model name (default: llama3.2:3b). In
                          dapr-agents 1.x the model is configured on the
                          Dapr Conversation component (not on the agent
                          constructor); SPRING_MODEL flows into the
                          deployed conversation-*.yaml component metadata
                          and is kept here for telemetry / agent-card
                          rendering.
  SPRING_LLM_PROVIDER   — Provider type label used for telemetry / agent
                          card description (e.g. ``ollama``, ``openai``).
                          The actual Dapr Conversation component name is
                          ``llm-provider`` by convention (overridable via
                          SPRING_LLM_COMPONENT).
  SPRING_LLM_COMPONENT  — Optional override for the Dapr Conversation
                          component name (default: ``llm-provider``).
  SPRING_SYSTEM_PROMPT  — System prompt assembled by the platform (optional)
  AGENT_PORT            — A2A server listen port (default: 8999)
"""

from __future__ import annotations

import asyncio
import logging
import os
from typing import Any, Awaitable, Callable, Tuple

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
from dapr_agents import AgentRunner
from dapr_agents import DurableAgent as Agent
from dapr_agents.llm import DaprChatClient

from a2a_server import DEFAULT_PORT, create_a2a_app
from mcp_bridge import create_tool_proxy, discover_tools

logging.basicConfig(
    level=logging.INFO,
    format="%(asctime)s %(levelname)s [%(name)s] %(message)s",
)
logger = logging.getLogger("dapr-agent")

DEFAULT_LLM_COMPONENT = "llm-provider"

AgentFactory = Callable[[], Awaitable[Tuple["Agent", "AgentRunner"]]]


class DaprAgentExecutor(AgentExecutor):
    """A2A executor that delegates to a Dapr Agents ``Agent`` instance.

    In dapr-agents 1.x, ``DurableAgent`` is workflow-driven and is invoked
    via ``AgentRunner.run(agent, payload)`` rather than the legacy
    ``agent.run(text)`` shim. The runner schedules the agent's workflow on
    the Dapr workflow runtime and waits for the serialized output.

    Construction is *lazy*: the executor is handed an async factory that
    builds the agent + runner on first invocation. This keeps the A2A
    server boot fast (the agent card is purely static — see
    :func:`a2a_server.build_agent_card`) and makes the ``GET
    /.well-known/agent-card.json`` smoke probe succeed immediately even
    when no Dapr sidecar is reachable. If a sidecar is present the first
    invocation pays the one-off construction cost; subsequent ones reuse
    the cached agent.
    """

    def __init__(
        self,
        factory: "AgentFactory",
    ) -> None:
        self._factory = factory
        self._agent: Agent | None = None
        self._runner: AgentRunner | None = None
        self._lock = asyncio.Lock()

    async def _ensure_built(self) -> tuple[Agent, AgentRunner]:
        if self._agent is not None and self._runner is not None:
            return self._agent, self._runner
        async with self._lock:
            if self._agent is None or self._runner is None:
                self._agent, self._runner = await self._factory()
            return self._agent, self._runner

    async def execute(
        self,
        context: RequestContext,
        event_queue: EventQueue,
    ) -> None:
        """Run the agentic loop for a single A2A task."""
        task = context.current_task or new_task(context.message)
        await event_queue.enqueue_event(task)

        # Extract user text from the incoming A2A message.
        user_text = ""
        if context.message and context.message.parts:
            for part in context.message.parts:
                if part.text:
                    user_text += part.text

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
            agent, runner = await self._ensure_built()
            result = await runner.run(
                agent,
                payload={"task": user_text},
                wait=True,
            )
            result_text = str(result) if result else ""

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


def _build_agent_kwargs() -> dict[str, Any]:
    """Build the kwargs passed to the dapr-agents 1.x ``DurableAgent``.

    Split out from :func:`_build_agent` so unit tests can validate the
    constructed kwargs against the real ``DurableAgent.__init__`` signature
    without standing up Dapr or doing tool discovery.
    """
    component_name = os.environ.get("SPRING_LLM_COMPONENT", DEFAULT_LLM_COMPONENT)
    system_prompt = os.environ.get("SPRING_SYSTEM_PROMPT", "")

    instructions = ["You are a helpful AI assistant."]
    if system_prompt:
        instructions = [system_prompt]

    # In dapr-agents 1.x the model is configured on the Dapr Conversation
    # component itself (via the deployed conversation-*.yaml metadata), not
    # on the DurableAgent constructor — the legacy ``model`` kwarg was
    # removed in 1.0 (Spring Voyage issue #1110). The Dapr Conversation
    # component name is ``llm-provider`` by convention; SPRING_LLM_COMPONENT
    # overrides if a deployment ever renames it.
    llm_client = DaprChatClient(component_name=component_name)

    return {
        "name": "SpringDaprAgent",
        "role": "AI Assistant",
        "goal": "Complete tasks using available tools and LLM reasoning",
        "instructions": instructions,
        "tools": [],
        "llm": llm_client,
    }


async def _build_agent() -> Agent:
    """Discover MCP tools and build the Dapr Agent instance."""
    mcp_endpoint = os.environ.get("SPRING_MCP_ENDPOINT", "")
    mcp_token = os.environ.get("SPRING_AGENT_TOKEN", "")
    model = os.environ.get("SPRING_MODEL", "llama3.2:3b")
    provider = os.environ.get("SPRING_LLM_PROVIDER", "ollama")

    agent_kwargs = _build_agent_kwargs()
    tools: list = list(agent_kwargs["tools"])

    if mcp_endpoint and mcp_token:
        try:
            tool_defs = await discover_tools(mcp_endpoint, mcp_token)
            for td in tool_defs:
                proxy = create_tool_proxy(td, mcp_endpoint, mcp_token)
                tools.append(proxy)
            logger.info("Loaded %d MCP tool proxies", len(tools))
        except Exception:
            logger.exception("Failed to discover MCP tools; continuing without tools")
    else:
        logger.warning("SPRING_MCP_ENDPOINT or SPRING_AGENT_TOKEN not set; running without MCP tools")

    agent_kwargs["tools"] = tools

    agent = Agent(**agent_kwargs)

    logger.info(
        "Dapr Agent built: provider=%s, model=%s, component=%s, tools=%d",
        provider,
        model,
        agent_kwargs["llm"].component_name,
        len(tools),
    )
    return agent


async def _default_factory() -> tuple[Agent, AgentRunner]:
    """Build the agent + runner the first time the executor is invoked.

    Construction is deferred so the A2A server's static agent card is
    served without waiting on Dapr metadata calls — keeps the readiness
    smoke probe fast even when no sidecar is reachable. The runner's
    workflow runtime is started eagerly inside the factory so a startup
    failure surfaces on first invocation rather than mid-task.
    """
    agent = await _build_agent()
    runner = AgentRunner()
    try:
        runner.workflow(agent)
        logger.info("Agent workflow runtime started")
    except Exception:
        logger.warning(
            "Failed to start agent workflow runtime; subsequent agent "
            "invocations will fail until a Dapr sidecar is reachable.",
            exc_info=True,
        )
    return agent, runner


def main() -> None:
    """Start the Dapr Agent with A2A server.

    The A2A application is mounted with a *lazy* executor: the underlying
    DurableAgent + AgentRunner are only constructed on the first
    ``message/send`` call. This lets ``GET /.well-known/agent-card.json``
    answer immediately even when no Dapr sidecar is reachable (the boot-
    time smoke contract), and keeps `dapr-agents`'s ~60 s sidecar-metadata
    probe off the critical-path of container startup.
    """
    port = int(os.environ.get("AGENT_PORT", str(DEFAULT_PORT)))

    executor = DaprAgentExecutor(_default_factory)
    a2a_app = create_a2a_app(executor, port=port)

    logger.info("Starting Dapr Agent A2A server on port %d", port)
    uvicorn.run(a2a_app.build(), host="0.0.0.0", port=port)


if __name__ == "__main__":
    main()
