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

import re
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


# --- Correlation tokens ---------------------------------------------------
#
# Spring Voyage delivery is one-way and a participant-set IS a thread
# (ADR-0030/0053), so every brief the orchestrator sends to the same writer
# shares one thread — the reply's thread_id cannot say which delegation it
# answers. v1 correlates with an explicit token the orchestrator embeds in each
# brief and the package's peer agents are instructed to echo verbatim. ADR-0066
# §5 records the principled platform fix (an in_reply_to correlation id) as the
# next increment.

_REF_RE = re.compile(r"\[\[sv-ref:(?P<ref>[^\]\s]+)\]\]")


def correlation_id(edition_id: str, slot_id: str, stage: str) -> str:
    """Build the opaque correlation id for one delegation."""
    return f"{edition_id}::{slot_id}::{stage}"


def parse_correlation_id(ref: str) -> tuple[str, str, str] | None:
    """Inverse of :func:`correlation_id`. ``None`` when *ref* is not ours."""
    parts = ref.split("::")
    if len(parts) != 3 or not all(parts):
        return None
    return parts[0], parts[1], parts[2]


def embed_ref(ref: str, body: str) -> str:
    """Append the correlation token to a brief so the reply can carry it back."""
    return (
        f"{body}\n\n"
        f"When you reply, include this reference token verbatim so the desk can "
        f"route your work to the right slot: [[sv-ref:{ref}]]"
    )


def extract_ref(text: str) -> str | None:
    """Extract the correlation token a peer echoed in its reply, if present."""
    match = _REF_RE.search(text or "")
    return match.group("ref") if match else None


# --- Briefs ---------------------------------------------------------------


@dataclass(frozen=True)
class Delegation:
    """A unit of work the engine hands to the runtime to deliver.

    The graph never performs I/O; it ``interrupt()``s with a Delegation and the
    runtime (``app.py``) resolves the role to an address, embeds the
    correlation token, sends via ``sv.messaging.send``, and resumes the graph
    when the reply lands.
    """

    role: str
    """Directory role the work goes to (e.g. ``staff-writer``)."""

    body: str
    """The brief text (without the correlation token — the runtime adds it)."""

    correlation_id: str
    """Opaque id the runtime maps back to this slot+stage on reply."""

    stage: str
    """The pipeline stage this delegation represents."""


def build_brief(
    *,
    stage: str,
    edition_id: str,
    slot_id: str,
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
    return Delegation(
        role=role,
        body=body,
        correlation_id=correlation_id(edition_id, slot_id, stage),
        stage=stage,
    )
