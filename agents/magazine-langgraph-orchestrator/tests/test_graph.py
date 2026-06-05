"""Tests for the LangGraph per-slot pipeline (ADR-0066).

Skipped when LangGraph is not installed (the image and CI install it); run
locally against an isolated venv.
"""

from __future__ import annotations

import pytest

pytest.importorskip("langgraph")

from langgraph.checkpoint.memory import MemorySaver  # noqa: E402
from langgraph.types import Command  # noqa: E402

from orchestrator import pipeline  # noqa: E402
from orchestrator.graph import build_slot_graph, pending_interrupt  # noqa: E402


def _config(edition_id: str, slot_id: str) -> dict:
    return {"configurable": {"thread_id": f"{edition_id}:{slot_id}"}}


def test_slot_pipeline_interrupt_resume_sequence():
    """A slot walks draft → fact_check → copy_edit → package, one interrupt per
    stage, each delegation addressed to the right role, then completes."""
    graph = build_slot_graph(MemorySaver())
    cfg = _config("ed1", "slot-1")

    result = graph.invoke(
        {
            "edition_id": "ed1",
            "slot_id": "slot-1",
            "slot_title": "City budget",
            "theme": "Local government",
            "artifact": None,
            "stages_done": [],
        },
        cfg,
    )

    seen_stages = []
    seen_roles = []
    carries_prior_artifact = []
    # Walk every stage by feeding a synthetic artifact back on each resume.
    for i in range(len(pipeline.SLOT_STAGES)):
        delegation = pending_interrupt(result)
        assert delegation is not None, f"expected interrupt at stage index {i}"
        seen_stages.append(delegation["stage"])
        seen_roles.append(delegation["role"])
        # The brief carries no correlation token (platform-native correlation).
        assert "sv-ref" not in delegation["body"]
        # The brief for a non-first stage carries the prior artifact inline
        # (the full piece, never a pointer — ADR-0030).
        carries_prior_artifact.append("Current piece:" in delegation["body"])
        result = graph.invoke(
            Command(resume=f"artifact-after-{delegation['stage']}"), cfg
        )

    assert seen_stages == list(pipeline.SLOT_STAGES)
    assert seen_roles == [
        "staff-writer",
        "fact-checker",
        "copy-editor",
        "audience-editor",
    ]
    # draft has no prior artifact; every later stage carries one.
    assert carries_prior_artifact == [False, True, True, True]

    # Graph reached END — no further interrupt, final artifact is the last reply.
    assert pending_interrupt(result) is None
    assert result["artifact"] == "artifact-after-package"
    assert result["stages_done"] == list(pipeline.SLOT_STAGES)
