"""The orchestration MCP server (ADR-0066 §6 Option B) — tool registration and
dispatch to the coordinator. The live Claude ↔ MCP loop is verified on deploy;
here we confirm the six tools are exposed and forward to the coordinator.

Skipped when the `mcp` package is not installed.
"""

from __future__ import annotations

import pytest

pytest.importorskip("mcp")

from orchestrator.tools import build_tool_server  # noqa: E402

_EXPECTED = {
    "start_edition",
    "get_status",
    "active_editions",
    "cancel_edition",
    "approve_edition",
    "revise_edition",
}


class _StubCoordinator:
    """Records calls; stands in for Coordinator (duck-typed)."""

    def __init__(self):
        self.calls: list[tuple] = []

    async def start_edition(self, *, theme, slots, report_to, briefs=None):
        self.calls.append(
            ("start_edition", theme, tuple(slots), report_to, tuple(briefs or ()))
        )
        return "edition-xyz"

    def get_status(self, edition_id):
        self.calls.append(("get_status", edition_id))
        return {"found": True, "edition_id": edition_id, "running": True}

    def active_editions(self):
        self.calls.append(("active_editions",))
        return []

    async def cancel_edition(self, edition_id):
        self.calls.append(("cancel_edition", edition_id))
        return {"ok": True, "edition_id": edition_id}

    async def approve_edition(self, edition_id):
        self.calls.append(("approve_edition", edition_id))
        return {"ok": True}

    async def revise_edition(self, edition_id, notes):
        self.calls.append(("revise_edition", edition_id, notes))
        return {"ok": True}


@pytest.mark.asyncio
async def test_registers_all_six_tools():
    server = build_tool_server(_StubCoordinator(), port=12399)
    names = {t.name for t in await server.list_tools()}
    assert names == _EXPECTED


@pytest.mark.asyncio
async def test_start_edition_dispatches_to_coordinator():
    stub = _StubCoordinator()
    server = build_tool_server(stub, port=12399)
    await server.call_tool(
        "start_edition",
        {
            "theme": "City hall",
            "slots": ["Budget", "Zoning"],
            "report_to": "unit:director",
        },
    )
    assert (
        "start_edition",
        "City hall",
        ("Budget", "Zoning"),
        "unit:director",
        (),
    ) in stub.calls


@pytest.mark.asyncio
async def test_start_edition_forwards_per_story_briefs():
    # #3088: the director's per-story direction must reach the coordinator (and
    # thence the writers), not be dropped at the tool boundary.
    stub = _StubCoordinator()
    server = build_tool_server(stub, port=12399)
    await server.call_tool(
        "start_edition",
        {
            "theme": "Tiny Joys",
            "slots": ["Coffee"],
            "briefs": ["~150 words, no research, warm vignette"],
            "report_to": "unit:director",
        },
    )
    assert (
        "start_edition",
        "Tiny Joys",
        ("Coffee",),
        "unit:director",
        ("~150 words, no research, warm vignette",),
    ) in stub.calls


@pytest.mark.asyncio
async def test_status_and_cancel_dispatch():
    stub = _StubCoordinator()
    server = build_tool_server(stub, port=12399)
    await server.call_tool("get_status", {"edition_id": "e1"})
    await server.call_tool("cancel_edition", {"edition_id": "e1"})
    assert ("get_status", "e1") in stub.calls
    assert ("cancel_edition", "e1") in stub.calls
