"""Option B (ADR-0066 §6): the engine receives; Claude manages via tools.

Covers the orchestration tools (start / status / active / cancel / approve /
revise) directly, the autonomous data plane (matched replies advance the graph
without Claude), and the control-plane routing (unmatched → the injected Claude
``invoke``). Skipped when LangGraph is not installed.
"""

from __future__ import annotations

import json

import pytest

pytest.importorskip("langgraph")

from langgraph.checkpoint.memory import MemorySaver  # noqa: E402

from orchestrator import mcp as mcp_tools  # noqa: E402
from orchestrator.coordinator import (  # noqa: E402
    MAX_REPLY_ATTEMPTS,
    Coordinator,
    DeliveryError,
)
from orchestrator.graph import build_slot_graph  # noqa: E402
from orchestrator.state import (  # noqa: E402
    PHASE_CANCELLED,
    PHASE_PUBLISHING,
    PHASE_SIGNOFF,
    OrchestratorStore,
)

_MEMBERS = [
    {"address": "agent:staffwriter", "kind": "agent", "roles": ["staff-writer"]},
    {"address": "agent:factchecker", "kind": "agent", "roles": ["fact-checker"]},
    {"address": "agent:copyeditor", "kind": "agent", "roles": ["copy-editor"]},
    {"address": "agent:audienceeditor", "kind": "agent", "roles": ["audience-editor"]},
    {
        "address": "agent:productioneditor",
        "kind": "agent",
        "roles": ["production-editor"],
    },
    {"address": "human:reader", "kind": "human", "roles": ["approver"]},
]


def _ok(artifact: str = "artifact") -> str:
    """The structured 'ok' envelope a specialist must return (#3088)."""
    return json.dumps({"status": "ok", "artifact": artifact})


def _blocked(reason: str) -> str:
    return json.dumps({"status": "blocked", "reason": reason})


class FakeMcp:
    """Records sends and serves a canned directory. ``send`` returns the created
    message id (msg-N) the engine correlates against a later reply's
    ``in_reply_to``."""

    def __init__(self, members=None):
        self.sends: list[tuple[list[str], str, str | None]] = []
        self._members = _MEMBERS if members is None else members

    async def call_tool_json(self, token, name, arguments):
        if name == mcp_tools.SEND_TOOL:
            self.sends.append(
                (arguments["recipients"], arguments["message"], arguments.get("reason"))
            )
            return {"messageId": f"msg-{len(self.sends)}", "deliveries": []}
        if name == mcp_tools.GET_SELF_TOOL:
            return {"parent_uuids": ["unit-1"]}
        if name == mcp_tools.LIST_TOOL:
            return self._members
        return {}

    def last_send(self):
        return self.sends[-1]


def _coord(tmp_path, invoke=None, members=None):
    return Coordinator(
        store=OrchestratorStore(str(tmp_path)),
        mcp=FakeMcp(members),
        graph=build_slot_graph(MemorySaver()),
        token="durable-tok",
        invoke=invoke,
    )


def _addr(send):
    return send[0][0]


async def _reply(coord, in_reply_to, *, text=None, artifact="artifact"):
    """A specialist reply naming the brief id it answers. Wraps `artifact` in the
    required structured 'ok' envelope (#3088) unless raw `text` is supplied."""
    body = text if text is not None else _ok(artifact)
    return await coord.handle(
        message_id="m", sender_address="agent:peer", text=body, in_reply_to=in_reply_to
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
    await _reply(coord, assemble_id, artifact="THE ASSEMBLED EDITION")
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
    await _reply(
        coord, assemble_id, artifact="ASSEMBLED"
    )  # production's assembled edition
    return eid


@pytest.mark.asyncio
async def test_approve_publishes(tmp_path):
    coord = _coord(tmp_path)
    eid = await _drive_to_signoff(coord)
    before = len(coord._mcp.sends)
    out = await coord.approve_edition(eid)
    # Publish is now a correlated step: the producer returns the final cut, then
    # the engine delivers it to the reader (#3088).
    assert out["ok"] and out["phase"] == PHASE_PUBLISHING
    assert len(coord._mcp.sends) == before + 1
    assert _addr(coord._mcp.last_send()) == "agent:productioneditor"
    assert "Publish" in coord._mcp.last_send()[1]

    # The producer returns the final edition → the engine delivers it to the
    # human reader (so the reader's response re-enters the control plane).
    publish_id = f"msg-{len(coord._mcp.sends)}"
    await _reply(coord, publish_id, artifact="THE FINAL EDITION")
    assert _addr(coord._mcp.last_send()) == "human:reader"
    assert "THE FINAL EDITION" in coord._mcp.last_send()[1]
    assert coord.get_status(eid)["phase"] == "published"


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
        message_id="m",
        sender_address="agent:peer",
        text=_ok("draft"),
        in_reply_to="msg-1",
    )
    assert called is False  # the data plane never touches Claude


@pytest.mark.asyncio
async def test_unmatched_with_no_invoke_is_ignored(tmp_path):
    coord = _coord(tmp_path)  # no control-plane handler
    out = await coord.handle(message_id="m", sender_address="unit:director", text="hi")
    assert "ignored" in out


# ----- error funnelling (#3086) -------------------------------------------


@pytest.mark.asyncio
async def test_start_edition_unfilled_role_raises_and_cancels(tmp_path):
    # The directory holds no staff-writer, so the first delegation cannot be
    # delivered. start_edition must STOP and surface the failure to its caller
    # (Claude) rather than report a live pipeline.
    coord = _coord(
        tmp_path,
        members=[{"address": "agent:factchecker", "roles": ["fact-checker"]}],
    )
    with pytest.raises(DeliveryError) as excinfo:
        await coord.start_edition(theme="T", slots=["x"], report_to="unit:director")
    assert "staff-writer" in str(excinfo.value)
    # Nothing delivered; the half-started edition is cancelled, not "running".
    assert coord._mcp.sends == []
    assert coord.active_editions() == []


@pytest.mark.asyncio
async def test_autonomous_delivery_failure_funnels_to_control_plane(tmp_path):
    invoked: list[tuple[str, str]] = []

    async def fake_invoke(text, *, sender_address):
        invoked.append((text, sender_address))
        return "The edition is blocked — I've let the director know."

    # Only a staff-writer exists: the draft is delivered, but the next stage
    # (fact-check) has no member, so the autonomous plane funnels the error to
    # the control-plane LLM instead of silently stalling.
    coord = _coord(
        tmp_path,
        invoke=fake_invoke,
        members=[{"address": "agent:staffwriter", "roles": ["staff-writer"]}],
    )
    await coord.start_edition(theme="T", slots=["x"], report_to="unit:director")
    assert _addr(coord._mcp.last_send()) == "agent:staffwriter"  # draft delivered

    # The writer replies → engine advances to fact-check, which cannot deliver.
    await _reply(coord, "msg-1", artifact="draft")

    # The error was funnelled to the control-plane LLM, naming the unfilled role,
    assert invoked, "control plane was not invoked on a data-plane failure"
    assert "fact-checker" in invoked[0][0]
    # ...and the Managing Editor's reply was carried to the director.
    assert coord._mcp.last_send()[0] == ["unit:director"]
    assert "director" in (coord._mcp.last_send()[2] or "").lower()


# ----- off-golden-path replies (the structured-reply gate, #3088) ---------


@pytest.mark.asyncio
async def test_blocked_reply_funnels_to_control_plane(tmp_path):
    invoked: list[str] = []

    async def fake_invoke(text, *, sender_address):
        invoked.append(text)
        return "I told the director the writer is blocked."

    coord = _coord(tmp_path, invoke=fake_invoke)
    await coord.start_edition(theme="T", slots=["x"], report_to="unit:director")
    before = len(coord._mcp.sends)

    # The writer cannot proceed and says so in the structured form — the engine
    # must NOT fold it as the artifact and advance; it funnels to the LLM.
    out = await _reply(coord, "msg-1", text=_blocked("the source pack never arrived"))
    assert invoked, "a blocked reply must reach the control plane"
    assert "the source pack never arrived" in invoked[0]
    assert coord._mcp.last_send()[0] == ["unit:director"]  # ME's reply to director
    # The graph did not advance to fact-check (only the funnel send happened).
    assert _addr(coord._mcp.sends[before]) == "unit:director"
    assert out


@pytest.mark.asyncio
async def test_unparseable_reply_retries_then_escalates(tmp_path):
    invoked: list[str] = []

    async def fake_invoke(text, *, sender_address):
        invoked.append(text)
        return "escalated to the director"

    coord = _coord(tmp_path, invoke=fake_invoke)
    await coord.start_edition(theme="T", slots=["x"], report_to="unit:director")
    assert _addr(coord._mcp.last_send()) == "agent:staffwriter"  # msg-1 draft brief

    # Each unparseable reply re-delegates to the SAME writer (attempts 1..N-1),
    # without escalating — up to the cutoff.
    for attempt in range(1, MAX_REPLY_ATTEMPTS):
        await _reply(coord, f"msg-{attempt}", text="sorry, I can't do JSON")
        assert not invoked, f"escalated too early at attempt {attempt}"
        assert _addr(coord._mcp.last_send()) == "agent:staffwriter"
        assert "retry" in (coord._mcp.last_send()[2] or "")

    # The final unparseable reply hits the cutoff → funnel to the control plane.
    out = await _reply(coord, f"msg-{MAX_REPLY_ATTEMPTS}", text="still not JSON")
    assert invoked, "after the retry cutoff the failure must reach the control plane"
    assert "draft" in invoked[0].lower()
    assert coord._mcp.last_send()[0] == ["unit:director"]
    assert out  # a non-empty summary is returned for the specialist's turn


@pytest.mark.asyncio
async def test_funnel_error_headless_is_terse_and_silent(tmp_path):
    # Head-less data plane (no invoke): the funnel degrades to a terse summary
    # and sends nothing — it must not raise.
    coord = _coord(tmp_path)  # invoke=None
    eid = await coord.start_edition(theme="T", slots=["x"], report_to="unit:director")
    edition = coord._store.get_edition(eid)
    before = len(coord._mcp.sends)
    out = await coord._funnel_error(edition, "No team member holds the role 'q'.")
    assert "q" in out
    assert len(coord._mcp.sends) == before  # nothing sent without a control plane
