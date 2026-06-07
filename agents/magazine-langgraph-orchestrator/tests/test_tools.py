"""The orchestration MCP server (ADR-0066 §6, #3078) — tool registration and
dispatch to the coordinator. The live Claude ↔ MCP loop is verified on deploy;
here we confirm the registry-built tools are exposed and forward to the
coordinator.

The published surface is the opt-in command registry (``commands.py``): the six
edition-level lifecycle commands are always present, and a tool is added for
every LangGraph node the workflow has explicitly annotated control-plane. The
default pipeline annotates none, so the server's surface is the lifecycle six —
but the assertions here are written as a **superset** check so an annotated node
adds a tool rather than breaking the suite (#3078).

Skipped when the `mcp` package is not installed.
"""

from __future__ import annotations

import pytest

pytest.importorskip("mcp")

from orchestrator.commands import build_command_registry, control_plane_metadata  # noqa: E402
from orchestrator.tools import build_tool_server  # noqa: E402

#: The always-present edition-level lifecycle commands (a lossless refactor of
#: the six tools the server used to write out by hand). The published surface is
#: a *superset* of these — the same six today, plus any annotated graph node.
_LIFECYCLE = {
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
async def test_registers_lifecycle_commands():
    # The default surface (no graph, so no annotated nodes) is exactly the
    # lifecycle six — asserted as a superset so an annotated graph node ADDS a
    # tool here instead of breaking the suite (#3078).
    server = build_tool_server(_StubCoordinator(), port=12399)
    names = {t.name for t in await server.list_tools()}
    assert names >= _LIFECYCLE
    assert names == _LIFECYCLE  # nothing else when no node is annotated


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


# --- graph-derived tools (#3078 seam) --------------------------------------
#
# A test-only fake compiled graph that mimics the LangGraph shape the registry
# reflects over: ``graph.builder.nodes`` is a dict of node specs, each carrying
# the ``metadata=`` it was added with. The default magazine pipeline annotates
# no node, so these fixtures stand in for "the day an author opts a node in."


class _FakeNodeSpec:
    def __init__(self, metadata=None):
        self.metadata = metadata
        self.runnable = None


class _FakeBuilder:
    def __init__(self, nodes):
        self.nodes = nodes


class _FakeGraph:
    """A graph with one annotated node (``rush_slot``) and one plain stage node
    (``draft``), so a single fixture proves both the surface and the gate."""

    def __init__(self):
        self.builder = _FakeBuilder(
            {
                "draft": _FakeNodeSpec(metadata=None),
                "rush_slot": _FakeNodeSpec(
                    metadata=control_plane_metadata(
                        name="rush_slot",
                        summary="Skip the slot straight to packaging.",
                        kind="command",
                    )
                ),
            }
        )


def _rush_handler_factory(calls=None):
    """Build a factory whose handler records its call into *calls* and has a
    typed signature so FastMCP can derive a schema. The production wiring routes
    the node through the coordinator; here we only need an introspectable
    signature plus a way to assert the handler ran."""

    def factory(node_name, descriptor):
        async def rush_slot(edition_id: str, slot_id: str) -> dict:
            if calls is not None:
                calls.append((node_name, edition_id, slot_id))
            return {"node": node_name, "edition_id": edition_id, "slot_id": slot_id}

        return rush_slot

    return factory


@pytest.mark.asyncio
async def test_annotated_node_surfaces_as_tool_with_schema():
    registry = build_command_registry(
        _StubCoordinator(), _FakeGraph(), handler_factory=_rush_handler_factory()
    )
    server = build_tool_server(_StubCoordinator(), registry=registry, port=12399)
    tools = {t.name: t for t in await server.list_tools()}

    # The annotated node is exposed alongside the lifecycle six.
    assert set(tools) == _LIFECYCLE | {"rush_slot"}

    rush = tools["rush_slot"]
    assert rush.description == "Skip the slot straight to packaging."
    # Schema derived from the handler's type hints (FastMCP mechanism).
    props = rush.inputSchema["properties"]
    assert set(props) == {"edition_id", "slot_id"}
    assert props["edition_id"]["type"] == "string"
    assert props["slot_id"]["type"] == "string"
    assert set(rush.inputSchema["required"]) == {"edition_id", "slot_id"}


@pytest.mark.asyncio
async def test_unannotated_node_does_not_surface():
    # ``draft`` (an un-annotated pipeline stage) must NOT become a tool — the
    # data plane stays closed by default (ADR-0066 §6).
    registry = build_command_registry(
        _StubCoordinator(), _FakeGraph(), handler_factory=_rush_handler_factory()
    )
    server = build_tool_server(_StubCoordinator(), registry=registry, port=12399)
    names = {t.name for t in await server.list_tools()}
    assert "draft" not in names
    assert names == _LIFECYCLE | {"rush_slot"}


@pytest.mark.asyncio
async def test_annotated_node_dispatches_through_its_handler():
    calls: list[tuple] = []
    registry = build_command_registry(
        _StubCoordinator(),
        _FakeGraph(),
        handler_factory=_rush_handler_factory(calls),
    )
    server = build_tool_server(_StubCoordinator(), registry=registry, port=12399)
    await server.call_tool("rush_slot", {"edition_id": "e1", "slot_id": "s1"})
    # The annotated node's handler ran with the LLM-supplied identifiers.
    assert ("rush_slot", "e1", "s1") in calls
