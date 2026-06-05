"""Unit tests for the durable orchestrator store (tmp-dir backed)."""

from __future__ import annotations

from orchestrator.state import PHASE_DRAFTING, OrchestratorStore


def _store(tmp_path):
    return OrchestratorStore(str(tmp_path))


def test_create_and_reload_edition(tmp_path):
    store = _store(tmp_path)
    edition = store.create_edition(
        edition_id="ed-1",
        theme="Local news",
        slot_titles=["City budget", "School board"],
        report_to="unit:director",
        origin_message_id="m0",
        first_stage="draft",
    )
    assert edition.phase == PHASE_DRAFTING
    assert list(edition.slots) == ["slot-1", "slot-2"]
    assert edition.slots["slot-1"].title == "City budget"
    assert edition.slots["slot-1"].stage == "draft"

    # Reload from disk via a fresh store instance — durability across restarts.
    reloaded = OrchestratorStore(str(tmp_path)).get_edition("ed-1")
    assert reloaded is not None
    assert reloaded.theme == "Local news"
    assert reloaded.slots["slot-2"].title == "School board"
    assert reloaded.report_to == "unit:director"
    assert reloaded.origin_message_id == "m0"


def test_get_missing_edition_returns_none(tmp_path):
    assert _store(tmp_path).get_edition("nope") is None


def test_all_slots_done(tmp_path):
    store = _store(tmp_path)
    edition = store.create_edition(
        edition_id="ed-2",
        theme="t",
        slot_titles=["a", "b"],
        report_to="x",
        origin_message_id="m",
        first_stage="draft",
    )
    assert not edition.all_slots_done()
    edition.slots["slot-1"].done = True
    assert not edition.all_slots_done()
    edition.slots["slot-2"].done = True
    assert edition.all_slots_done()
    store.save_edition(edition)
    assert OrchestratorStore(str(tmp_path)).get_edition("ed-2").all_slots_done()


def test_correlation_put_pop_is_one_shot(tmp_path):
    store = _store(tmp_path)
    store.put_correlation(
        "ed::slot-1::draft", edition_id="ed", slot_id="slot-1", stage="draft"
    )
    entry = store.pop_correlation("ed::slot-1::draft")
    assert entry == {"edition_id": "ed", "slot_id": "slot-1", "stage": "draft"}
    # Second pop returns None — refs are consumed once.
    assert store.pop_correlation("ed::slot-1::draft") is None


def test_correlation_survives_reload(tmp_path):
    store = _store(tmp_path)
    store.put_correlation("r1", edition_id="ed", slot_id="slot-1", stage="draft")
    assert OrchestratorStore(str(tmp_path)).pop_correlation("r1") is not None
