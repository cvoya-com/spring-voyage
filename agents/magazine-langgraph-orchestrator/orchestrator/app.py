"""
SDK wiring for the magazine orchestrator (ADR-0066).

Thin platform-facing layer: build the engine in ``initialize()``, route each
inbound message to the :class:`~orchestrator.coordinator.Coordinator` in
``on_message()``. All orchestration logic lives in ``coordinator.py`` (which is
SDK-free and unit-tested against the real LangGraph engine with a fake MCP
client); this module only adapts the SDK ``Message``/``IAgentContext`` types to
the coordinator's plain-field interface.
"""

from __future__ import annotations

import logging
import os

from spring_voyage_agent_sdk import (
    IAgentContext,
    Message,
    Response,
    ShutdownReason,
    run,
)

from orchestrator import mcp as mcp_tools
from orchestrator.coordinator import Coordinator
from orchestrator.graph import build_slot_graph, make_sqlite_checkpointer
from orchestrator.state import OrchestratorStore

logging.basicConfig(
    level=logging.INFO,
    format="%(asctime)s %(levelname)s [%(name)s] %(message)s",
)
logger = logging.getLogger("magazine-orchestrator")

_coordinator: Coordinator | None = None
_context: IAgentContext | None = None


async def initialize(context: IAgentContext) -> None:
    """Build the engine: MCP client, the durable store + SQLite checkpointer on
    the workspace volume, and the compiled LangGraph slot pipeline."""
    global _coordinator, _context
    _context = context

    checkpoint_dir = os.path.join(context.workspace_path, "orchestrator")
    os.makedirs(checkpoint_dir, exist_ok=True)
    checkpointer = make_sqlite_checkpointer(
        os.path.join(checkpoint_dir, "checkpoints.sqlite")
    )

    _coordinator = Coordinator(
        store=OrchestratorStore(context.workspace_path),
        mcp=mcp_tools.McpClient(context.mcp_url),
        graph=build_slot_graph(checkpointer),
    )
    logger.info(
        "Magazine orchestrator initialized (workspace=%s)", context.workspace_path
    )


async def on_message(message: Message):
    """Route one inbound message through the coordinator. The SDK final Response
    is a diagnostic trace only — all real output goes via sv.messaging."""
    assert _coordinator is not None, "initialize() must run before on_message"

    # ADR-0066 §2: authenticate with THIS turn's MCP token, not a cached one.
    token = message.mcp_token or (_context.mcp_token if _context else "")
    if not token:
        yield Response(error="No MCP token on this turn; cannot reach platform tools.")
        return

    envelope = message.envelope
    sender_address = envelope.from_address if envelope else message.sender.id
    message_id = (
        envelope.message_id if envelope and envelope.message_id else message.message_id
    )

    try:
        summary = await _coordinator.handle(
            thread_id=message.thread_id,
            message_id=message_id,
            sender_address=sender_address,
            text=message.text,
            token=token,
        )
        yield Response(text=summary, final=True)
    except Exception as exc:  # noqa: BLE001 — surface as a turn error, don't crash the process.
        logger.exception("Orchestration failed for message %s", message.message_id)
        yield Response(error=f"Orchestration failed: {exc}")


async def on_shutdown(reason: ShutdownReason) -> None:
    logger.info("Magazine orchestrator shutting down (reason=%s)", reason.value)


def main() -> None:
    run(initialize=initialize, on_message=on_message, on_shutdown=on_shutdown)


if __name__ == "__main__":
    main()
