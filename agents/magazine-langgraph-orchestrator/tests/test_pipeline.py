"""Unit tests for the pure pipeline definitions (no LangGraph needed)."""

from __future__ import annotations

from orchestrator import pipeline


def test_stage_order_and_roles():
    assert pipeline.SLOT_STAGES == ("draft", "fact_check", "copy_edit", "package")
    assert pipeline.STAGE_ROLE["draft"] == "staff-writer"
    assert pipeline.STAGE_ROLE["package"] == "audience-editor"


def test_next_stage():
    assert pipeline.next_stage("draft") == "fact_check"
    assert pipeline.next_stage("copy_edit") == "package"
    assert pipeline.next_stage("package") is None


def test_build_brief_first_stage_has_no_prior_artifact():
    d = pipeline.build_brief(
        stage="draft",
        slot_title="City budget",
        theme="Local",
        artifact=None,
    )
    assert d.role == "staff-writer"
    assert d.stage == "draft"
    assert "Current piece:" not in d.body
    assert "City budget" in d.body
    # Correlation is platform-native — no token is embedded in the brief.
    assert "sv-ref" not in d.body


def test_build_brief_later_stage_carries_artifact_inline():
    d = pipeline.build_brief(
        stage="fact_check",
        slot_title="City budget",
        theme="Local",
        artifact="THE DRAFT TEXT",
    )
    assert d.role == "fact-checker"
    assert "Current piece:" in d.body
    assert "THE DRAFT TEXT" in d.body


def test_build_brief_carries_the_per_story_brief_into_every_stage():
    # #3088: the director's per-story direction (length, sourcing, tone) must
    # reach the writers. Without it a "150-word, no-research vignette" came back
    # as a long researched feature. The brief rides every stage, draft included.
    draft = pipeline.build_brief(
        stage="draft",
        slot_title="The perfect cup of morning coffee",
        theme="Tiny Joys",
        artifact=None,
        slot_brief="~150 words. Warm, sensory vignette. No research or citations.",
    )
    assert "Editor's brief" in draft.body
    assert "No research or citations" in draft.body
    # And it persists into a later stage too.
    fact = pipeline.build_brief(
        stage="fact_check",
        slot_title="The perfect cup of morning coffee",
        theme="Tiny Joys",
        artifact="DRAFT",
        slot_brief="~150 words. No research.",
    )
    assert "Editor's brief" in fact.body
    assert "No research" in fact.body


def test_build_brief_omits_brief_section_when_absent():
    d = pipeline.build_brief(
        stage="draft", slot_title="X", theme="Y", artifact=None, slot_brief=""
    )
    assert "Editor's brief" not in d.body


def test_build_brief_later_stage_demands_the_complete_piece_back():
    # #3088: a stage receives only what the previous one returns, so every stage
    # must hand back the whole evolving piece. A fact-checker that replied with
    # findings alone once dropped the article and the rest of the line had
    # nothing to work on; the brief now states the contract explicitly.
    d = pipeline.build_brief(
        stage="fact_check",
        slot_title="City budget",
        theme="Local",
        artifact="THE DRAFT TEXT",
    )
    assert "complete updated piece" in d.body
    assert "next stage" in d.body
