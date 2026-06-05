"""
The LangGraph per-slot pipeline (ADR-0066).

This is the engine: a real LangGraph ``StateGraph`` whose nodes are the
specialist stages a story slot passes through. Each stage node ``interrupt()``s
with a :class:`~orchestrator.pipeline.Delegation` — handing the work back to the
runtime to deliver over ``sv.messaging`` — and resumes when the peer's reply is
fed back in as the resume value. The graph performs no I/O itself; it only
decides *what* the next delegation is and folds returned artifacts forward.

A SQLite checkpointer on the workspace volume persists each slot's graph state
across turns and restarts, keyed by graph ``thread_id`` =
``"<edition_id>:<slot_id>"``. The always-on process holds state in memory while
live; the checkpoint is the crash-and-restart source of truth.

Imports are limited to LangGraph + the pure ``pipeline`` module so this engine
core is unit-testable without the SDK, A2A, or a live platform.
"""

from __future__ import annotations

import sqlite3
from typing import Any, TypedDict

from langgraph.checkpoint.sqlite import SqliteSaver
from langgraph.graph import END, START, StateGraph
from langgraph.types import interrupt

from orchestrator import pipeline


class SlotState(TypedDict, total=False):
    """Graph state for one story slot moving through the pipeline."""

    edition_id: str
    slot_id: str
    slot_title: str
    theme: str
    artifact: str | None  # the current piece; updated after each stage
    stages_done: list[str]


def _stage_node(stage: str):
    """Build the node function for one pipeline *stage*.

    The node composes the delegation for this stage from the artifact the
    previous stage produced, ``interrupt()``s to hand it to the runtime, and
    stores the peer's reply as the new artifact. On resume LangGraph re-enters
    the node and ``interrupt()`` returns the resume value instead of pausing —
    so everything before it (``build_brief``) must be pure, which it is.
    """

    def node(state: SlotState) -> dict[str, Any]:
        delegation = pipeline.build_brief(
            stage=stage,
            edition_id=state["edition_id"],
            slot_id=state["slot_id"],
            slot_title=state["slot_title"],
            theme=state["theme"],
            artifact=state.get("artifact"),
        )
        reply = interrupt(
            {
                "role": delegation.role,
                "body": delegation.body,
                "correlation_id": delegation.correlation_id,
                "stage": delegation.stage,
            }
        )
        return {
            "artifact": reply,
            "stages_done": [*state.get("stages_done", []), stage],
        }

    return node


def build_slot_graph(checkpointer: Any):
    """Build and compile the per-slot pipeline graph.

    Topology: ``START → draft → fact_check → copy_edit → package → END`` — one
    node per :data:`orchestrator.pipeline.SLOT_STAGES` entry, in order.
    """
    builder: StateGraph = StateGraph(SlotState)

    prev = START
    for stage in pipeline.SLOT_STAGES:
        builder.add_node(stage, _stage_node(stage))
        builder.add_edge(prev, stage)
        prev = stage
    builder.add_edge(prev, END)

    return builder.compile(checkpointer=checkpointer)


def make_sqlite_checkpointer(db_path: str) -> SqliteSaver:
    """Open a durable SQLite checkpointer at *db_path* on the workspace volume.

    ``check_same_thread=False`` because the SDK's uvicorn loop may touch the
    saver from worker threads; the engine serialises graph access per slot, so
    there is no concurrent write to one slot's checkpoint.
    """
    conn = sqlite3.connect(db_path, check_same_thread=False)
    saver = SqliteSaver(conn)
    saver.setup()
    return saver


def pending_interrupt(result: dict[str, Any]) -> dict[str, Any] | None:
    """Extract the delegation payload from a paused graph result.

    Returns the interrupt's ``value`` (the delegation dict) when the graph is
    paused at a stage, or ``None`` when the run reached ``END`` (slot complete).
    """
    interrupts = result.get("__interrupt__")
    if not interrupts:
        return None
    first = interrupts[0]
    value = getattr(first, "value", None)
    return value if isinstance(value, dict) else None
