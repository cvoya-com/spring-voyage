"""
The orchestration MCP server (ADR-0066 §6, Option B).

The engine co-hosts this local MCP server and points the co-hosted Claude's
``--mcp-config`` at it. When the engine hands Claude an unmatched (director)
message, Claude *manages* the orchestration by calling these tools — starting an
edition, checking its status, cancelling it, recording an approval or revision.
The engine runs the data plane (specialist delegations and their replies)
itself; Claude never touches that.

The tools are thin wrappers over :class:`~orchestrator.coordinator.Coordinator`
so the orchestration logic stays in one place (unit-tested directly). This
server binds to localhost only — it is reachable solely by the Claude process
inside the same container.
"""

from __future__ import annotations

import logging

from mcp.server.fastmcp import FastMCP

from orchestrator.coordinator import Coordinator

logger = logging.getLogger("magazine-orchestrator.tools")

# Default localhost port the orchestration MCP server listens on. Distinct from
# AGENT_PORT (the A2A server). Overridable via ORCHESTRATION_TOOLS_PORT.
DEFAULT_TOOLS_PORT = 8771

# The streamable-http endpoint path FastMCP mounts (its default).
TOOLS_MCP_PATH = "/mcp"


def build_tool_server(
    coordinator: Coordinator, *, host: str = "127.0.0.1", port: int = DEFAULT_TOOLS_PORT
) -> FastMCP:
    """Build the local MCP server exposing the orchestration tools, backed by
    *coordinator*. Bind to localhost only."""
    server = FastMCP("orchestration", host=host, port=port)

    @server.tool()
    async def start_edition(
        theme: str,
        slots: list[str],
        report_to: str,
        briefs: list[str] | None = None,
    ) -> str:
        """Start a new magazine edition and return its edition_id.

        theme: the edition's overarching subject.
        slots: the ordered list of story-slot titles to commission (1-6).
        briefs: the director's COMPLETE direction for each story, aligned 1:1
            with slots — angle, target length, tone, sourcing rules, and any
            non-negotiables, verbatim from the director's brief. ALWAYS pass this
            whenever the director specified anything beyond a bare title: the
            writers see ONLY what you put here, so a "150-word, no-research
            vignette" left out of briefs comes back as a long researched piece.
            Use "" for a slot the director left open.
        report_to: the director's address from the inbound message — where the
            assembled edition is brought for sign-off.

        Returns the edition_id. Use it for later get_status / cancel_edition /
        approve_edition / revise_edition calls (active_editions also lists it).
        """
        return await coordinator.start_edition(
            theme=theme, slots=slots, report_to=report_to, briefs=briefs
        )

    @server.tool()
    def get_status(edition_id: str) -> dict:
        """The live status of one edition: its phase and each slot's stage."""
        return coordinator.get_status(edition_id)

    @server.tool()
    def active_editions() -> list[dict]:
        """The editions still running. Empty when nothing is in motion — call
        this to answer a progress question before any edition has started."""
        return coordinator.active_editions()

    @server.tool()
    async def cancel_edition(edition_id: str) -> dict:
        """Cancel a running edition."""
        return await coordinator.cancel_edition(edition_id)

    @server.tool()
    async def approve_edition(edition_id: str) -> dict:
        """Record the director's approval of the assembled edition and release it
        to production to publish. Valid only once an edition is awaiting sign-off."""
        return await coordinator.approve_edition(edition_id)

    @server.tool()
    async def revise_edition(edition_id: str, notes: str) -> dict:
        """Send the director's revision notes back to production for a revise
        pass. Valid only once an edition is awaiting sign-off."""
        return await coordinator.revise_edition(edition_id, notes)

    logger.info("Orchestration MCP server built on %s:%d%s", host, port, TOOLS_MCP_PATH)
    return server
