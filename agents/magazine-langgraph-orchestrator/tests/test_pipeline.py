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


def test_correlation_id_round_trip():
    ref = pipeline.correlation_id("ed-1", "slot-2", "fact_check")
    assert pipeline.parse_correlation_id(ref) == ("ed-1", "slot-2", "fact_check")


def test_parse_correlation_id_rejects_foreign_tokens():
    assert pipeline.parse_correlation_id("not-ours") is None
    assert pipeline.parse_correlation_id("a::b") is None


def test_embed_and_extract_ref():
    ref = "ed-1::slot-1::draft"
    body = pipeline.embed_ref(ref, "Write the story.")
    assert "Write the story." in body
    assert pipeline.extract_ref(body) == ref


def test_extract_ref_absent():
    assert pipeline.extract_ref("just a normal reply") is None


def test_build_brief_first_stage_has_no_prior_artifact():
    d = pipeline.build_brief(
        stage="draft",
        edition_id="ed-1",
        slot_id="slot-1",
        slot_title="City budget",
        theme="Local",
        artifact=None,
    )
    assert d.role == "staff-writer"
    assert d.stage == "draft"
    assert "Current piece:" not in d.body
    assert "City budget" in d.body


def test_build_brief_later_stage_carries_artifact_inline():
    d = pipeline.build_brief(
        stage="fact_check",
        edition_id="ed-1",
        slot_id="slot-1",
        slot_title="City budget",
        theme="Local",
        artifact="THE DRAFT TEXT",
    )
    assert d.role == "fact-checker"
    assert "Current piece:" in d.body
    assert "THE DRAFT TEXT" in d.body
