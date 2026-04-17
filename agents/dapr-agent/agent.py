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
  SPRING_MODEL          — LLM model name (default: llama3.2:3b)
  SPRING_LLM_PROVIDER   — Dapr Conversation component name (default: ollama)
  SPRING_SYSTEM_PROMPT  — System prompt assembled by the platform (optional)
  AGENT_PORT            — A2A server listen port (default: 8999)
"""

from __future__ import annotations

import asyncio
import logging
import os
from typing import Any

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
from dapr_agents import DurableAgent as Agent

from a2a_server import DEFAULT_PORT, create_a2a_app
from mcp_bridge import create_tool_proxy, discover_tools

logging.basicConfig(
    level=logging.INFO,
    format="%(asctime)s %(levelname)s [%(name)s] %(message)s",
)
logger = logging.getLogger("dapr-agent")


class DaprAgentExecutor(AgentExecutor):
    """A2A executor that delegates to a Dapr Agents ``Agent`` instance."""

    def __init__(self, agent: Agent) -> None:
        self._agent = agent

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
            result = await self._agent.run(user_text)
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


async def _build_agent() -> Agent:
    """Discover MCP tools and build the Dapr Agent instance."""
    mcp_endpoint = os.environ.get("SPRING_MCP_ENDPOINT", "")
    mcp_token = os.environ.get("SPRING_AGENT_TOKEN", "")
    model = os.environ.get("SPRING_MODEL", "llama3.2:3b")
    provider = os.environ.get("SPRING_LLM_PROVIDER", "ollama")
    system_prompt = os.environ.get("SPRING_SYSTEM_PROMPT", "")

    tools: list = []
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

    instructions = ["You are a helpful AI assistant."]
    if system_prompt:
        instructions = [system_prompt]

    # Build the DurableAgent kwargs. `llm` names the Dapr Conversation component
    # (component metadata.name, not the building-block type) that the agent's
    # sidecar exposes; passing it explicitly pins the provider so mis-routed or
    # unconfigured components fail loudly at startup instead of silently falling
    # back to DurableAgent's environment-driven default. `model` likewise pins
    # the model the component will request — required for multi-model Ollama
    # deployments and to make the provider/model knob visible on every turn.
    agent_kwargs: dict[str, Any] = {
        "name": "SpringDaprAgent",
        "role": "AI Assistant",
        "goal": "Complete tasks using available tools and LLM reasoning",
        "instructions": instructions,
        "tools": tools,
    }
    if provider:
        agent_kwargs["llm"] = provider
    if model:
        agent_kwargs["model"] = model

    agent = Agent(**agent_kwargs)

    logger.info(
        "Dapr Agent built: provider=%s, model=%s, tools=%d",
        provider,
        model,
        len(tools),
    )
    return agent


def main() -> None:
    """Start the Dapr Agent with A2A server."""
    port = int(os.environ.get("AGENT_PORT", str(DEFAULT_PORT)))

    agent = asyncio.run(_build_agent())
    executor = DaprAgentExecutor(agent)
    a2a_app = create_a2a_app(executor, port=port)

    logger.info("Starting Dapr Agent A2A server on port %d", port)
    uvicorn.run(a2a_app.build(), host="0.0.0.0", port=port)


if __name__ == "__main__":
    main()
