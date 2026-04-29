#!/usr/bin/env python3
"""
Spring Voyage Dapr Agent — migrated onto the Spring Voyage Agent SDK.

Implements the three SDK lifecycle hooks (initialize / on_message /
on_shutdown) and hands them to the SDK runtime.

Why no Dapr Workflow / DurableAgent: ADR 0029 Stage 0 (issue #1199
follow-up). An ephemeral agent is defined by container-lifetime-tied-to-
completion-of-work, not by any specific durability mechanism. Going
through ``dapr_agents.DurableAgent`` requires the Dapr workflow runtime
(actor placement + scheduler) — those services live on ``spring-net`` per
ADR 0028, but the per-launch daprd sidecar joins the per-workflow bridge
and the tenant network only, so workflow start consistently times out
with ``DaprBuiltInActorNotFoundRetries`` retries on the sidecar. The
agentic loop is implemented inline below using ``DaprChatClient`` + the
MCP tool proxies; durability of in-flight state is the workspace volume
ADR 0029 § "Durable state" defines, not the workflow runtime.

Configuration
-------------
The Spring Voyage Agent SDK reads the canonical env vars at initialize()
time (SPRING_TENANT_ID, SPRING_AGENT_ID, etc. — see
docs/specs/agent-runtime-boundary.md §2.2.1).  The Dapr-specific vars
below are still honoured for backwards-compatibility with existing
deployments:

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
  SPRING_SYSTEM_PROMPT  — System prompt assembled by the platform
                          (optional; falls back to
                          agent_definition.instructions if unset).
  SPRING_AGENT_MAX_STEPS — Maximum tool-call rounds before forcing the
                          loop to terminate (default: 12). Guards
                          against runaway loops without imposing a wall
                          clock that would fight the upstream LLM
                          timeout.

  The MCP endpoint and token are now read from IAgentContext
  (SPRING_MCP_URL / SPRING_MCP_TOKEN) instead of the legacy
  SPRING_MCP_ENDPOINT / SPRING_AGENT_TOKEN names.  Both names are
  supported for now; the canonical names take precedence.
"""

from __future__ import annotations

import json
import logging
import os
from dataclasses import dataclass
from typing import List

from dapr_agents.llm import DaprChatClient
from dapr_agents.tool.base import AgentTool
from dapr_agents.types.message import (
    AssistantMessage,
    BaseMessage,
    SystemMessage,
    ToolMessage,
    UserMessage,
)
from spring_voyage_agent import IAgentContext, Message, Response, ShutdownReason, run

from mcp_bridge import create_tool_proxy, discover_tools

logging.basicConfig(
    level=logging.INFO,
    format="%(asctime)s %(levelname)s [%(name)s] %(message)s",
)
logger = logging.getLogger("dapr-agent")

DEFAULT_LLM_COMPONENT = "llm-provider"
DEFAULT_MAX_STEPS = 12

# Module-level cache populated by initialize(); on_message reads it.
_agent_build: "AgentBuild | None" = None


@dataclass
class AgentBuild:
    """Cached agent runtime — the LLM client + the resolved tool list."""

    llm: DaprChatClient
    tools: List[AgentTool]
    system_prompt: str
    tools_by_name: dict[str, AgentTool]


# ---------------------------------------------------------------------------
# Lifecycle hook: initialize
# ---------------------------------------------------------------------------


async def initialize(context: IAgentContext) -> None:
    """Wire up the Dapr LLM client and discover MCP tools.

    SDK spec §1.1: called exactly once before any on_message invocation.
    The result is cached in the module-level ``_agent_build``.
    """
    global _agent_build

    # MCP endpoint: prefer the canonical IAgentContext fields; fall back to
    # the legacy SPRING_MCP_ENDPOINT / SPRING_AGENT_TOKEN names used in the
    # pre-SDK agent so existing deployments are not broken.
    mcp_endpoint = context.mcp_url or os.environ.get("SPRING_MCP_ENDPOINT", "")
    mcp_token = context.mcp_token or os.environ.get("SPRING_AGENT_TOKEN", "")

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
            "MCP endpoint or token not set; running without MCP tools",
        )

    tools_by_name = {_resolve_tool_name(t, f"tool-{i}"): t for i, t in enumerate(tools)}

    # Resolve system prompt: explicit env var wins; fall back to
    # agent_definition.instructions from the mounted context file.
    system_prompt = os.environ.get("SPRING_SYSTEM_PROMPT", "")
    if not system_prompt:
        system_prompt = (
            context.agent_definition.get("instructions", "") or "You are a helpful AI assistant."
        )

    logger.info(
        "Dapr Agent initialized (workflow-free per ADR 0029 Stage 0): "
        "provider=%s, model=%s, component=%s, tools=%d",
        provider,
        model,
        component_name,
        len(tools),
    )

    _agent_build = AgentBuild(
        llm=llm_client,
        tools=tools,
        system_prompt=system_prompt,
        tools_by_name=tools_by_name,
    )


# ---------------------------------------------------------------------------
# Lifecycle hook: on_message
# ---------------------------------------------------------------------------


async def on_message(message: Message):
    """Run the Dapr agentic loop for one inbound A2A message.

    SDK spec §1.2: called once per inbound message; yields Response chunks.
    The tool-calling loop runs against DaprChatClient (Dapr Conversation
    building block) and the MCP tool proxies wired up in initialize().
    """
    assert _agent_build is not None, "initialize() must complete before on_message"

    user_text = message.text or ""

    try:
        result_text = await _run_agentic_loop(_agent_build, user_text)
        yield Response(text=result_text, final=True)
    except Exception as exc:
        logger.exception("Agent loop failed for message %s", message.message_id)
        yield Response(error=f"Agent loop failed: {exc}")


# ---------------------------------------------------------------------------
# Lifecycle hook: on_shutdown
# ---------------------------------------------------------------------------


async def on_shutdown(reason: ShutdownReason) -> None:
    """Flush in-progress state on SIGTERM.

    SDK spec §1.3: called exactly once on container termination.
    The Dapr-based agent stores its durable state in the workspace volume
    (SPRING_WORKSPACE_PATH) rather than in memory, so the shutdown hook
    is minimal: close any open resources and log the reason.
    """
    logger.info("Dapr Agent shutting down (reason=%s)", reason.value)
    # Tool proxies use ephemeral httpx.AsyncClient instances (one per call);
    # there are no persistent connections to drain here.


# ---------------------------------------------------------------------------
# Internal helpers (unchanged from the pre-SDK agent)
# ---------------------------------------------------------------------------


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


def _resolve_tool_name(tool: AgentTool, fallback: str) -> str:
    """Pick the canonical name the LLM tool-call will reference."""
    return getattr(tool, "name", None) or fallback


# ---------------------------------------------------------------------------
# Entry point
# ---------------------------------------------------------------------------


def main() -> None:
    """Start the Dapr Agent via the Spring Voyage Agent SDK runtime."""
    run(
        initialize=initialize,
        on_message=on_message,
        on_shutdown=on_shutdown,
    )


if __name__ == "__main__":
    main()
