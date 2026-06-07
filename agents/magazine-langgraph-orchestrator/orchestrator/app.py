"""
SDK wiring for the magazine orchestrator (ADR-0066 §6, Option B).

``initialize()`` builds the engine, hosts the local orchestration MCP server on
localhost, and writes the ``.mcp.json`` the co-hosted Claude uses to reach it.
``on_message()`` routes each inbound message to the Coordinator: a matched
specialist reply advances the graph autonomously; an unmatched (director)
message is handed to Claude — invoked *with the orchestration tools* — whose
reply is the agent's response.

All orchestration logic lives in ``coordinator.py``; this module only adapts the
SDK types, hosts the tool server, and wires the control-plane invoke.
"""

from __future__ import annotations

import asyncio
import contextlib
import json
import logging
import os

import uvicorn

from spring_voyage_agent_sdk import (
    IAgentContext,
    Message,
    Response,
    ShutdownReason,
    llm,
    run,
)

from orchestrator import mcp as mcp_tools
from orchestrator.commands import build_command_registry
from orchestrator.coordinator import Coordinator
from orchestrator.graph import build_slot_graph, make_sqlite_checkpointer
from orchestrator.reply import body_from_payload
from orchestrator.state import OrchestratorStore
from orchestrator.tools import DEFAULT_TOOLS_PORT, TOOLS_MCP_PATH, build_tool_server

logging.basicConfig(
    level=logging.INFO,
    format="%(asctime)s %(levelname)s [%(name)s] %(message)s",
)
logger = logging.getLogger("magazine-orchestrator")

_coordinator: Coordinator | None = None
_context: IAgentContext | None = None
_tool_server_task: asyncio.Task | None = None


def _inbound_body(message: Message) -> str:
    """The sender's actual message body — the structured envelope payload
    (ADR-0066 §3), not the full rendered envelope prose ``message.text``.

    The engine forwards a specialist's reply as the next stage's "current piece"
    and parses it as the structured reply, so it must carry the real body — clean
    copy or the ``{"status","artifact"}`` envelope — not the ``You received a
    message…`` boilerplate that ``message.text`` wraps around the payload (#3088).
    The extraction (content/text wrapper vs. a parsed structured object vs. the
    rendered-prose fallback) lives in :func:`orchestrator.reply.body_from_payload`
    so it is unit-testable without the SDK."""
    envelope = message.envelope
    payload = envelope.payload if envelope else None
    return body_from_payload(payload, message.text)


def _tools_port() -> int:
    raw = os.environ.get("ORCHESTRATION_TOOLS_PORT")
    return int(raw) if raw and raw.isdigit() else DEFAULT_TOOLS_PORT


def _write_mcp_config(workspace_path: str, port: int) -> str:
    """Write the ``.mcp.json`` that points Claude at the local orchestration
    server. Only the localhost tool server — Claude discovers and tracks
    editions through the tools (``active_editions`` / ``get_status``), so it
    needs no platform MCP here."""
    config = {
        "mcpServers": {
            "orchestration": {
                "type": "http",
                "url": f"http://127.0.0.1:{port}{TOOLS_MCP_PATH}",
            }
        }
    }
    path = os.path.join(workspace_path, ".spring", "orchestration-mcp.json")
    os.makedirs(os.path.dirname(path), exist_ok=True)
    with open(path, "w", encoding="utf-8") as handle:
        json.dump(config, handle)
    return path


class _QuietServer(uvicorn.Server):
    """A uvicorn server that does not grab the process signals — the SDK
    runtime owns SIGTERM, and this second server (the tool MCP) must not
    override its shutdown handling."""

    @contextlib.contextmanager
    def capture_signals(self):  # type: ignore[override]
        yield


async def _serve_tool_server(server) -> None:
    config = uvicorn.Config(
        server.streamable_http_app(),
        host="127.0.0.1",
        port=_tools_port(),
        log_config=None,
    )
    await _QuietServer(config).serve()


async def initialize(context: IAgentContext) -> None:
    """Build the engine + checkpointer, host the orchestration tool server, and
    wire the control-plane Claude invoke."""
    global _coordinator, _context, _tool_server_task
    _context = context

    checkpoint_dir = os.path.join(context.workspace_path, "orchestrator")
    os.makedirs(checkpoint_dir, exist_ok=True)
    checkpointer = make_sqlite_checkpointer(
        os.path.join(checkpoint_dir, "checkpoints.sqlite")
    )

    port = _tools_port()
    mcp_config_path = _write_mcp_config(context.workspace_path, port)
    system_prompt_file = os.path.join(
        context.workspace_path, ".spring", "system-prompt.md"
    )

    async def invoke(text: str, *, sender_address: str) -> str:
        # The control plane: hand the director's message to Claude with the
        # orchestration tools (ADR-0066 §6 Option B). Claude's reply IS the
        # agent's response to the sender; the engine ships it. So Claude must
        # always end with a short natural-language reply and must not call a
        # messaging tool to reply nor send an empty message (#3086 finding 3),
        # and on a tool error it must say what failed rather than claim the
        # pipeline is running (#3086 finding 2).
        prompt = (
            f"Message from {sender_address}:\n\n{text}\n\n"
            "You manage the magazine's editions through your orchestration tools. "
            "Work out what this message means and act on it — it will not always "
            "be a clean approval:\n"
            "- The director approving an edition at sign-off → approve_edition.\n"
            "- The director, OR a reader after publication, asking for changes or "
            "withholding approval → revise_edition with their notes, so the "
            "edition is re-opened and reworked; do not treat a non-approval as an "
            "approval.\n"
            "- A reader accepting the published edition → thank them; no tool call.\n"
            "- The engine reporting that the pipeline stopped or a specialist "
            "could not finish → do not pretend it is running; decide whether to "
            "revise, cancel, or escalate, and say plainly what failed and what "
            "would unblock it.\n"
            "- Anything else (a status question, a new commission) → use "
            "get_status / active_editions / start_edition as fits.\n"
            "ALWAYS end with a short natural-language reply for the sender — that "
            "reply is delivered to them automatically, so never reply with empty "
            "text and do not call any messaging tool to reply."
        )
        return await llm.complete(
            prompt,
            system_prompt_file=system_prompt_file,
            mcp_config_path=mcp_config_path,
        )

    graph = build_slot_graph(checkpointer)
    _coordinator = Coordinator(
        store=OrchestratorStore(context.workspace_path),
        mcp=mcp_tools.McpClient(context.mcp_url),
        graph=graph,
        # ADR-0066 §2: seed the durable, agent-scoped MCP token (used by the
        # data-plane sv.messaging calls; refreshed per message).
        token=context.mcp_token or "",
        invoke=invoke,
    )

    # ADR-0066 §6 / #3078: the tool surface is built from the opt-in command
    # registry — the six lifecycle commands plus a tool for every graph node the
    # workflow has explicitly annotated control-plane. The default pipeline
    # annotates none, so this is the lifecycle six and the data plane stays
    # closed by default.
    registry = build_command_registry(_coordinator, graph)
    tool_server = build_tool_server(_coordinator, registry=registry, port=port)
    _tool_server_task = asyncio.create_task(_serve_tool_server(tool_server))
    logger.info(
        "Magazine orchestrator initialized (workspace=%s); tools on 127.0.0.1:%d",
        context.workspace_path,
        port,
    )


async def on_message(message: Message):
    """Route one inbound message through the coordinator. For a matched reply the
    summary is a diagnostic ack; for an unmatched message it is Claude's reply."""
    assert _coordinator is not None, "initialize() must run before on_message"

    token = message.mcp_token or (_context.mcp_token if _context else "")
    if not token:
        yield Response(error="No MCP token available; cannot reach platform tools.")
        return

    envelope = message.envelope
    sender_address = envelope.from_address if envelope else message.sender.id
    message_id = (
        envelope.message_id if envelope and envelope.message_id else message.message_id
    )
    in_reply_to = envelope.in_reply_to if envelope else None

    try:
        summary = await _coordinator.handle(
            message_id=message_id,
            sender_address=sender_address,
            text=_inbound_body(message),
            in_reply_to=in_reply_to,
            token=token,
        )
        # Never ship an empty response: the platform delivers it via
        # sv.messaging.respond_to, which rejects an empty message (#3086
        # finding 3). Fall back to a terse ack when the handler yields nothing.
        if not (summary or "").strip():
            summary = "Acknowledged."
        yield Response(text=summary, final=True)
    except Exception as exc:  # noqa: BLE001 — surface as a turn error, don't crash.
        logger.exception("Orchestration failed for message %s", message.message_id)
        yield Response(error=f"Orchestration failed: {exc}")


async def on_shutdown(reason: ShutdownReason) -> None:
    if _tool_server_task is not None:
        _tool_server_task.cancel()
    logger.info("Magazine orchestrator shutting down (reason=%s)", reason.value)


def main() -> None:
    run(initialize=initialize, on_message=on_message, on_shutdown=on_shutdown)


if __name__ == "__main__":
    main()
