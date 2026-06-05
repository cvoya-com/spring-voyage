"""Option B (ADR-0066 §6): the engine receives; Claude manages via tools.

Covers the orchestration tools (start / status / active / cancel / approve /
revise) directly, the autonomous data plane (matched replies advance the graph
without Claude), and the control-plane routing (unmatched → the injected Claude
``invoke``). Skipped when LangGraph is not installed.
"""

from __future__ import annotations

import pytest

pytest.importorskip("langgraph")

from langgraph.checkpoint.memory import MemorySaver  # noqa: E402

from orchestrator import mcp as mcp_tools  # noqa: E402
from orchestrator.coordinator import Coordinator  # noqa: E402
from orchestrator.graph import build_slot_graph  # noqa: E402
from orchestrator.state import (  # noqa: E402
    PHASE_CANCELLED,
    PHASE_PUBLISHED,
    PHASE_SIGNOFF,
    OrchestratorStore,
)

_MEMBERS = [
    {"address": "agent:staffwriter", "roles": ["staff-writer"]},
    {"address": "agent:factchecker", "roles": ["fact-checker"]},
    {"address": "agent:copyeditor", "roles": ["copy-editor"]},
    {"address": "agent:audienceeditor", "roles": ["audience-editor"]},
    {"address": "agent:productioneditor", "roles": ["production-editor"]},
]


class FakeMcp:
    """Records sends and serves a canned directory. ``send`` returns the created
    message id (msg-N) the engine correlates against a later reply's
    ``in_reply_to``."""

    def __init__(self):
        self.sends: list[tuple[list[str], str, str | None]] = []

    async def call_tool_json(self, token, name, arguments):
        if name == mcp_tools.SEND_TOOL:
            self.sends.append(
                (arguments["recipients"], arguments["message"], arguments.get("reason"))
            )
            return {"messageId": f"msg-{len(self.sends)}", "deliveries": []}
        if name == mcp_tools.GET_SELF_TOOL:
            return {"parent_uuids": ["unit-1"]}
        if name == mcp_tools.LIST_MEMBERS_TOOL:
            return _MEMBERS
        return {}

    def last_send(self):
        return self.sends[-1]


def _coord(tmp_path, invoke=None):
    return Coordinator(
        store=OrchestratorStore(str(tmp_path)),
        mcp=FakeMcp(),
        graph=build_slot_graph(MemorySaver()),
        token="durable-tok",
        invoke=invoke,
    )


def _addr(send):
    return send[0][0]


async def _reply(coord, in_reply_to, *, text="artifact"):
    """A specialist reply naming the brief id it answers."""
    return await coord.handle(
        message_id="m", sender_address="agent:peer", text=text, in_reply_to=in_reply_to
    )


# ----- the orchestration tools --------------------------------------------


@pytest.mark.asyncio
async def test_start_edition_mints_id_and_delegates(tmp_path):
    coord = _coord(tmp_path)
    mcp = coord._mcp
    edition_id = await coord.start_edition(
        theme="Local government",
        slots=["City budget", "School board"],
        report_to="unit:director",
    )
    # A fresh, opaque id — not a conversation/thread id.
    assert edition_id and edition_id != "unit:director" and len(edition_id) >= 16
    # Two slots → two draft briefs to the writer.
    assert len(mcp.sends) == 2
    assert all(_addr(s) == "agent:staffwriter" for s in mcp.sends)
    status = coord.get_status(edition_id)
    assert status["found"] and status["running"]
    assert [s["title"] for s in status["slots"]] == ["City budget", "School board"]


@pytest.mark.asyncio
async def test_two_editions_coexist(tmp_path):
    coord = _coord(tmp_path)
    a = await coord.start_edition(theme="A", slots=["a1"], report_to="unit:director")
    b = await coord.start_edition(theme="B", slots=["b1"], report_to="unit:director")
    assert a != b
    assert {e["edition_id"] for e in coord.active_editions()} == {a, b}


def test_get_status_unknown(tmp_path):
    coord = _coord(tmp_path)
    assert coord.get_status("nope") == {"found": False, "edition_id": "nope"}


def test_active_editions_empty_before_kickoff(tmp_path):
    coord = _coord(tmp_path)
    assert coord.active_editions() == []


@pytest.mark.asyncio
async def test_cancel_marks_terminal_and_excludes_from_active(tmp_path):
    coord = _coord(tmp_path)
    eid = await coord.start_edition(theme="T", slots=["x"], report_to="unit:director")
    out = await coord.cancel_edition(eid)
    assert out["ok"] and out["phase"] == PHASE_CANCELLED
    assert coord.active_editions() == []
    assert not coord.get_status(eid)["running"]
    # Cancel again → no-op (already terminal).
    assert (await coord.cancel_edition(eid))["ok"] is False


@pytest.mark.asyncio
async def test_cancel_unknown(tmp_path):
    coord = _coord(tmp_path)
    assert (await coord.cancel_edition("nope"))["ok"] is False


# ----- the autonomous data plane (matched replies, no Claude) -------------


@pytest.mark.asyncio
async def test_data_plane_drives_to_signoff_autonomously(tmp_path):
    coord = _coord(tmp_path)  # no invoke — purely the data plane
    mcp = coord._mcp
    eid = await coord.start_edition(
        theme="News", slots=["S1", "S2"], report_to="unit:director"
    )
    s1, s2 = "msg-1", "msg-2"

    # Slot 1 through its stages; the package reply finishes it (slot 2 open).
    await _reply(coord, s1)
    assert _addr(mcp.last_send()) == "agent:factchecker"
    await _reply(coord, f"msg-{len(mcp.sends)}")
    assert _addr(mcp.last_send()) == "agent:copyeditor"
    await _reply(coord, f"msg-{len(mcp.sends)}")
    assert _addr(mcp.last_send()) == "agent:audienceeditor"
    before = len(mcp.sends)
    await _reply(coord, f"msg-{len(mcp.sends)}")
    assert len(mcp.sends) == before

    # Slot 2; its package reply triggers assembly to production.
    await _reply(coord, s2)
    await _reply(coord, f"msg-{len(mcp.sends)}")
    await _reply(coord, f"msg-{len(mcp.sends)}")
    before = len(mcp.sends)
    await _reply(coord, f"msg-{len(mcp.sends)}")
    assert len(mcp.sends) == before + 1
    assert _addr(mcp.last_send()) == "agent:productioneditor"
    assemble_id = f"msg-{len(mcp.sends)}"

    # Production returns the assembled edition → sign-off goes to the DIRECTOR
    # via a NEW message to report_to (Option B: the engine has no director
    # message to respond_to).
    await _reply(coord, assemble_id, text="THE ASSEMBLED EDITION")
    assert _addr(mcp.last_send()) == "unit:director"
    assert "sign-off" in mcp.last_send()[1].lower()
    assert coord.get_status(eid)["phase"] == PHASE_SIGNOFF


@pytest.mark.asyncio
async def test_matched_reply_for_cancelled_edition_is_ignored(tmp_path):
    coord = _coord(tmp_path)
    await coord.start_edition(theme="T", slots=["x"], report_to="unit:director")
    # Cancel by id (the only active edition).
    eid = coord.active_editions()[0]["edition_id"]
    await coord.cancel_edition(eid)
    before = len(coord._mcp.sends)
    out = await _reply(coord, "msg-1")  # the draft brief's id — now moot
    assert "ignored" in out
    assert len(coord._mcp.sends) == before  # no further delegation, no leak to Claude


# ----- approve / revise tools (after sign-off) ----------------------------


async def _drive_to_signoff(coord):
    eid = await coord.start_edition(
        theme="T", slots=["only"], report_to="unit:director"
    )
    nxt = "msg-1"
    for _ in range(3):  # draft → fact_check → copy_edit → audience
        await _reply(coord, nxt)
        nxt = f"msg-{len(coord._mcp.sends)}"
    await _reply(coord, nxt)  # package reply → assemble to production
    assemble_id = f"msg-{len(coord._mcp.sends)}"
    await _reply(coord, assemble_id, text="ASSEMBLED")  # production's assembled edition
    return eid


@pytest.mark.asyncio
async def test_approve_publishes(tmp_path):
    coord = _coord(tmp_path)
    eid = await _drive_to_signoff(coord)
    before = len(coord._mcp.sends)
    out = await coord.approve_edition(eid)
    assert out["ok"] and out["phase"] == PHASE_PUBLISHED
    assert len(coord._mcp.sends) == before + 1
    assert _addr(coord._mcp.last_send()) == "agent:productioneditor"
    assert "Publish" in coord._mcp.last_send()[1]


@pytest.mark.asyncio
async def test_revise_sends_notes_to_production(tmp_path):
    coord = _coord(tmp_path)
    eid = await _drive_to_signoff(coord)
    out = await coord.revise_edition(eid, "Tighten the lede.")
    assert out["ok"]
    assert _addr(coord._mcp.last_send()) == "agent:productioneditor"
    assert "Tighten the lede." in coord._mcp.last_send()[1]


@pytest.mark.asyncio
async def test_approve_rejected_before_signoff(tmp_path):
    coord = _coord(tmp_path)
    eid = await coord.start_edition(theme="T", slots=["x"], report_to="unit:director")
    out = await coord.approve_edition(eid)  # still drafting
    assert out["ok"] is False


# ----- control-plane routing (unmatched → Claude) -------------------------


@pytest.mark.asyncio
async def test_unmatched_message_routes_to_invoke(tmp_path):
    seen = {}

    async def fake_invoke(text, *, sender_address):
        seen["text"] = text
        seen["sender"] = sender_address
        return "claude's reply"

    coord = _coord(tmp_path, invoke=fake_invoke)
    out = await coord.handle(
        message_id="q", sender_address="unit:director", text="what's your progress?"
    )
    assert out == "claude's reply"
    assert seen == {"text": "what's your progress?", "sender": "unit:director"}


@pytest.mark.asyncio
async def test_matched_reply_bypasses_invoke(tmp_path):
    called = False

    async def fake_invoke(text, *, sender_address):
        nonlocal called
        called = True
        return "x"

    coord = _coord(tmp_path, invoke=fake_invoke)
    await coord.start_edition(theme="T", slots=["x"], report_to="unit:director")
    await coord.handle(
        message_id="m", sender_address="agent:peer", text="draft", in_reply_to="msg-1"
    )
    assert called is False  # the data plane never touches Claude


@pytest.mark.asyncio
async def test_unmatched_with_no_invoke_is_ignored(tmp_path):
    coord = _coord(tmp_path)  # no control-plane handler
    out = await coord.handle(message_id="m", sender_address="unit:director", text="hi")
    assert "ignored" in out
