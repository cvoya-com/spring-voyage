"""
The orchestration MCP server (ADR-0066 §6, #3078).

The engine co-hosts this local MCP server and points the co-hosted Claude's
``--mcp-config`` at it. When the engine hands Claude an unmatched (director)
message, Claude *manages* the orchestration by calling these tools — starting an
edition, checking its status, cancelling it, recording an approval or revision.
The engine runs the data plane (specialist delegations and their replies)
itself; Claude never touches that.

The published tool surface is **not** a hand-maintained block of
``@server.tool()`` wrappers. It is built from an opt-in
:class:`~orchestrator.commands.EngineCommand` registry (``commands.py``) and
registered here one tool per descriptor via ``server.add_tool(...)``. FastMCP
derives each tool's input schema from the handler's type hints and uses the
descriptor's ``summary`` as the description — the same FastMCP mechanism the
hand-written tools relied on, so every schema (notably ``start_edition``'s
#3088-tuned ``briefs`` guidance) is reproduced unchanged. Discovery stays
ordinary MCP ``tools/list``; there is no second discovery RPC.

The registry always carries the six edition-level *lifecycle* commands and adds
a tool for each LangGraph node the workflow author has explicitly annotated
control-plane (``commands.graph_derived_commands``). The default magazine
pipeline annotates no node, so today's surface is exactly the lifecycle six —
the data plane stays closed by default (ADR-0066 §6).

The tools are thin descriptors over :class:`~orchestrator.coordinator.Coordinator`
methods so the orchestration logic stays in one place (unit-tested directly).
This server binds to localhost only — it is reachable solely by the Claude
process inside the same container.
"""

from __future__ import annotations

import logging

from mcp.server.fastmcp import FastMCP

from orchestrator.commands import EngineCommand, build_command_registry
from orchestrator.coordinator import Coordinator

logger = logging.getLogger("magazine-orchestrator.tools")

# Default localhost port the orchestration MCP server listens on. Distinct from
# AGENT_PORT (the A2A server). Overridable via ORCHESTRATION_TOOLS_PORT.
DEFAULT_TOOLS_PORT = 8771

# The streamable-http endpoint path FastMCP mounts (its default).
TOOLS_MCP_PATH = "/mcp"


def build_tool_server(
    coordinator: Coordinator,
    *,
    registry: list[EngineCommand] | None = None,
    graph: object | None = None,
    host: str = "127.0.0.1",
    port: int = DEFAULT_TOOLS_PORT,
) -> FastMCP:
    """Build the local MCP server exposing the orchestration tools. Bind to
    localhost only.

    The tool surface comes from a :class:`~orchestrator.commands.EngineCommand`
    registry. Pass *registry* directly (built by the caller from a coordinator +
    compiled graph), or pass *graph* to have it assembled here via
    :func:`~orchestrator.commands.build_command_registry`; when neither is given
    the registry is built from the lifecycle commands alone (no graph-derived
    tools), which is today's default surface.

    Each command is registered with FastMCP via ``add_tool`` — the input schema
    is derived from the handler's type hints and the ``summary`` is the tool
    description, so the published schemas match what the hand-written wrappers
    produced.
    """
    if registry is None:
        registry = build_command_registry(coordinator, graph)

    server = FastMCP("orchestration", host=host, port=port)
    for command in registry:
        server.add_tool(
            command.handler,
            name=command.name,
            description=command.summary,
        )

    logger.info(
        "Orchestration MCP server built on %s:%d%s with %d tool(s): %s",
        host,
        port,
        TOOLS_MCP_PATH,
        len(registry),
        ", ".join(command.name for command in registry),
    )
    return server
