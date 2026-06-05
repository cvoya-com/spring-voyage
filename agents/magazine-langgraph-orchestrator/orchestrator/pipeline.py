"""
Pure pipeline definitions for the magazine orchestrator (ADR-0066).

This module is deliberately free of LangGraph, A2A, and SDK imports so the
pipeline topology, brief construction, and correlation-token handling can be
unit-tested in isolation. ``graph.py`` consumes it to build the LangGraph
per-slot StateGraph; ``app.py`` consumes it for delegation + reply correlation.

The per-slot pipeline is the repetitive, engine-shaped part of the magazine
workflow: every story slot flows through the same ordered specialist stages.
The edition-level join (all slots packaged → assemble → sign-off → publish)
lives in ``app.py``/``state.py`` because it is bookkeeping over many slots, not
a per-slot state machine.
"""

from __future__ import annotations

from dataclasses import dataclass

# --- Per-slot pipeline stages --------------------------------------------

DRAFT = "draft"
FACT_CHECK = "fact_check"
COPY_EDIT = "copy_edit"
PACKAGE = "package"

#: Ordered stages every story slot passes through. The engine advances a slot
#: one stage at a time, delegating each to the role below and folding the
#: returned artifact back in before the next stage.
SLOT_STAGES: tuple[str, ...] = (DRAFT, FACT_CHECK, COPY_EDIT, PACKAGE)

#: The peer role (a directory role label) each stage delegates to.
STAGE_ROLE: dict[str, str] = {
    DRAFT: "staff-writer",
    FACT_CHECK: "fact-checker",
    COPY_EDIT: "copy-editor",
    PACKAGE: "audience-editor",
}

#: Imperative description of what each stage asks its specialist to do.
STAGE_ASK: dict[str, str] = {
    DRAFT: "Report and write the first draft of this story.",
    FACT_CHECK: "Verify every checkable claim and audit the sourcing; return the "
    "draft with your findings.",
    COPY_EDIT: "Polish the language and house style without reshaping the story.",
    PACKAGE: "Write the final headline and promo line and return the packaged piece.",
}

#: Role that assembles the edition once every slot is packaged.
PRODUCTION_ROLE = "production-editor"


def next_stage(stage: str) -> str | None:
    """Return the stage after *stage*, or ``None`` when the slot is complete."""
    idx = SLOT_STAGES.index(stage)
    return SLOT_STAGES[idx + 1] if idx + 1 < len(SLOT_STAGES) else None


# --- Briefs ---------------------------------------------------------------
#
# Correlation is platform-native (ADR-0066 §5): the orchestrator records the id
# `sv.messaging.send` returns for each brief, and matches a reply to the right
# slot+stage via the reply's `in_reply_to` (the message it answered). No token
# is embedded in the brief and the specialists echo nothing — they just reply.


@dataclass(frozen=True)
class Delegation:
    """A unit of work the engine hands to the runtime to deliver.

    The graph never performs I/O; it ``interrupt()``s with a Delegation and the
    runtime (``app.py``) resolves the role to an address, sends the body via
    ``sv.messaging.send``, records the returned message id, and resumes the
    graph when the correlated reply lands.
    """

    role: str
    """Directory role the work goes to (e.g. ``staff-writer``)."""

    body: str
    """The brief text — delivered verbatim; correlation is platform-native."""

    stage: str
    """The pipeline stage this delegation represents."""


def build_brief(
    *,
    stage: str,
    slot_title: str,
    theme: str,
    artifact: str | None,
) -> Delegation:
    """Compose the delegation for one stage of one slot.

    The full current artifact is carried in the body (never a pointer): every
    stage is memory-isolated and cannot see an earlier thread's draft
    (ADR-0030). ``artifact`` is ``None`` for the first stage (DRAFT).
    """
    role = STAGE_ROLE[stage]
    ask = STAGE_ASK[stage]
    header = f'Edition theme: "{theme}". Story slot: "{slot_title}".'
    if artifact:
        body = f"{header}\n\n{ask}\n\nCurrent piece:\n\n{artifact}"
    else:
        body = f"{header}\n\n{ask}"
    return Delegation(role=role, body=body, stage=stage)
