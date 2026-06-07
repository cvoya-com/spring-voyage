"""Unit tests for the orchestration command registry (``commands.py``, #3078).

These exercise the registry assembly directly — no MCP server, no LangGraph — so
they run even when the optional ``mcp`` package is absent. The registry has two
opt-in sources: the always-present lifecycle commands and the graph-derived
commands reflected from *annotated* graph nodes. The default magazine pipeline
annotates no node, so the graph-derived source ships empty (ADR-0066 §6).
"""

from __future__ import annotations

import pytest

from orchestrator.commands import (
    CONTROL_PLANE_METADATA_KEY,
    EngineCommand,
    build_command_registry,
    control_plane,
    control_plane_metadata,
    graph_derived_commands,
    lifecycle_commands,
)

_LIFECYCLE_NAMES = {
    "start_edition",
    "get_status",
    "active_editions",
    "cancel_edition",
    "approve_edition",
    "revise_edition",
}


class _StubCoordinator:
    """Bare coordinator surface the lifecycle descriptors bind to."""

    async def start_edition(self, *, theme, slots, report_to, briefs=None):
        return "edition-xyz"

    def get_status(self, edition_id):
        return {"edition_id": edition_id}

    def active_editions(self):
        return []

    async def cancel_edition(self, edition_id):
        return {"ok": True}

    async def approve_edition(self, edition_id):
        return {"ok": True}

    async def revise_edition(self, edition_id, notes):
        return {"ok": True}


# --- fake LangGraph shapes -------------------------------------------------


class _FakeNodeSpec:
    def __init__(self, metadata=None, runnable=None):
        self.metadata = metadata
        self.runnable = runnable


class _FakeBuilder:
    def __init__(self, nodes):
        self.nodes = nodes


class _FakeGraph:
    def __init__(self, nodes):
        self.builder = _FakeBuilder(nodes)


def _passthrough_factory(node_name, descriptor):
    def handler(edition_id: str) -> dict:
        return {"node": node_name, "edition_id": edition_id}

    return handler


# --- lifecycle commands ----------------------------------------------------


def test_lifecycle_commands_are_the_six_bound_to_coordinator():
    coord = _StubCoordinator()
    commands = lifecycle_commands(coord)
    assert {c.name for c in commands} == _LIFECYCLE_NAMES
    by_name = {c.name: c for c in commands}
    # Bound to the actual coordinator methods (lossless refactor).
    assert by_name["start_edition"].handler == coord.start_edition
    assert by_name["get_status"].handler == coord.get_status
    # kind is carried (the deferred Alt-B discovery seam).
    assert by_name["start_edition"].kind == "command"
    assert by_name["get_status"].kind == "query"
    assert by_name["active_editions"].kind == "query"
    # Lifecycle commands have no stage scope.
    assert all(c.stage_scope is None for c in commands)


def test_start_edition_summary_preserves_briefs_guidance():
    # #3088: the director's per-story direction guidance must survive the
    # refactor into the registry, verbatim, as the tool description.
    summary = {c.name: c.summary for c in lifecycle_commands(_StubCoordinator())}[
        "start_edition"
    ]
    assert "briefs: the director's COMPLETE direction for each story" in summary
    assert "aligned 1:1 with slots" in summary
    assert 'Use "" for a slot the director left open.' in summary


# --- graph-derived commands ------------------------------------------------


def test_default_pipeline_annotates_no_node_seam_ships_empty():
    # The real compiled magazine graph annotates no node, so reflection yields
    # nothing — the registry is the lifecycle six and the data plane is closed.
    from orchestrator.graph import build_slot_graph, make_sqlite_checkpointer

    graph = build_slot_graph(make_sqlite_checkpointer(":memory:"))
    derived = graph_derived_commands(graph, handler_factory=_passthrough_factory)
    assert derived == []
    registry = build_command_registry(_StubCoordinator(), graph)
    assert {c.name for c in registry} == _LIFECYCLE_NAMES


def test_only_annotated_nodes_yield_commands_via_metadata():
    graph = _FakeGraph(
        {
            "draft": _FakeNodeSpec(metadata=None),
            "rush": _FakeNodeSpec(
                metadata=control_plane_metadata(
                    name="rush", summary="Rush it.", kind="command"
                )
            ),
        }
    )
    derived = graph_derived_commands(graph, handler_factory=_passthrough_factory)
    assert [c.name for c in derived] == ["rush"]
    rush = derived[0]
    assert rush.summary == "Rush it."
    assert rush.kind == "command"
    assert rush.stage_scope == "rush"  # the node it derived from


def test_annotation_via_decorator_marker_is_reflected():
    # The @control_plane decorator form: the marker rides on the node callable
    # (node_spec.runnable.func), which reflection also reads.
    @control_plane(name="peek", summary="Peek.", kind="query")
    def node_fn(state):
        return {}

    class _RunnableHolder:
        def __init__(self, func):
            self.func = func

    graph = _FakeGraph({"peek": _FakeNodeSpec(runnable=_RunnableHolder(node_fn))})
    derived = graph_derived_commands(graph, handler_factory=_passthrough_factory)
    assert [c.name for c in derived] == ["peek"]
    assert derived[0].kind == "query"


def test_control_plane_metadata_shape():
    md = control_plane_metadata(name="x", summary="y", kind="command")
    assert md == {
        CONTROL_PLANE_METADATA_KEY: {"name": "x", "summary": "y", "kind": "command"}
    }


def test_control_plane_decorator_returns_function_unchanged_with_marker():
    @control_plane(name="x", summary="y")
    def fn(state):
        return state

    assert fn("z") == "z"  # still callable, unchanged behaviour
    assert fn.__control_plane__ == {"name": "x", "summary": "y", "kind": "command"}


def test_non_langgraph_graph_contributes_no_commands():
    # A graph object without the builder.nodes shape (or None) simply yields no
    # graph-derived commands rather than erroring.
    assert graph_derived_commands(None, handler_factory=_passthrough_factory) == []
    assert graph_derived_commands(object(), handler_factory=_passthrough_factory) == []


# --- registry assembly -----------------------------------------------------


def test_build_command_registry_merges_lifecycle_and_derived():
    graph = _FakeGraph(
        {
            "rush": _FakeNodeSpec(
                metadata=control_plane_metadata(name="rush", summary="Rush it.")
            )
        }
    )
    registry = build_command_registry(
        _StubCoordinator(), graph, handler_factory=_passthrough_factory
    )
    assert {c.name for c in registry} == _LIFECYCLE_NAMES | {"rush"}


def test_build_command_registry_without_graph_is_lifecycle_only():
    registry = build_command_registry(_StubCoordinator())
    assert {c.name for c in registry} == _LIFECYCLE_NAMES


def test_annotated_node_without_handler_factory_raises():
    # An annotated node with no factory to route it through the coordinator is a
    # wiring bug — the default factory refuses rather than registering a tool
    # that touches raw graph state.
    graph = _FakeGraph(
        {
            "rush": _FakeNodeSpec(
                metadata=control_plane_metadata(name="rush", summary="Rush it.")
            )
        }
    )
    with pytest.raises(ValueError, match="no handler_factory"):
        build_command_registry(_StubCoordinator(), graph)


def test_duplicate_command_name_raises():
    # A graph-derived command colliding with a lifecycle name is rejected.
    graph = _FakeGraph(
        {
            "x": _FakeNodeSpec(
                metadata=control_plane_metadata(name="get_status", summary="dup")
            )
        }
    )
    with pytest.raises(ValueError, match="duplicate command name"):
        build_command_registry(
            _StubCoordinator(), graph, handler_factory=_passthrough_factory
        )


def test_engine_command_is_frozen():
    cmd = EngineCommand(name="x", summary="y", handler=lambda: None)
    with pytest.raises(Exception):
        cmd.name = "z"  # type: ignore[misc]
