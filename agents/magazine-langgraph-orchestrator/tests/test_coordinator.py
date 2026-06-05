"""End-to-end orchestration test: the real LangGraph engine + a fake MCP client.

Drives a full two-slot edition from the director's kickoff, through every
specialist stage of every slot, through assembly, to the director's sign-off and
the publish hand-off — asserting the orchestrator delivers the right brief to the
right role at each step and tracks durable state across the (simulated) turns.

Skipped when LangGraph is not installed.
"""

from __future__ import annotations

import json

import pytest

pytest.importorskip("langgraph")

from langgraph.checkpoint.memory import MemorySaver  # noqa: E402

from orchestrator import mcp as mcp_tools  # noqa: E402
from orchestrator import pipeline  # noqa: E402
from orchestrator.coordinator import Coordinator  # noqa: E402
from orchestrator.graph import build_slot_graph  # noqa: E402
from orchestrator.state import PHASE_PUBLISHED, OrchestratorStore  # noqa: E402

# A directory of the magazine peers, keyed by role.
_MEMBERS = [
    {"address": "agent:staffwriter", "roles": ["staff-writer"]},
    {"address": "agent:factchecker", "roles": ["fact-checker"]},
    {"address": "agent:copyeditor", "roles": ["copy-editor"]},
    {"address": "agent:audienceeditor", "roles": ["audience-editor"]},
    {"address": "agent:productioneditor", "roles": ["production-editor"]},
]


class FakeMcp:
    """Records sends/responds and serves a canned directory — duck-types McpClient."""

    def __init__(self):
        self.sends: list[tuple[list[str], str, str | None]] = []
        self.responds: list[tuple[str, str]] = []

    async def call_tool(self, token, name, arguments):
        if name == mcp_tools.SEND_TOOL:
            self.sends.append(
                (arguments["recipients"], arguments["message"], arguments.get("reason"))
            )
            return "delivered"
        if name == mcp_tools.RESPOND_TO_TOOL:
            self.responds.append((arguments["message_id"], arguments["message"]))
            return "delivered"
        if name == mcp_tools.GET_SELF_TOOL:
            return json.dumps({"parent_uuids": ["unit-1"]})
        if name == mcp_tools.LIST_MEMBERS_TOOL:
            return json.dumps(_MEMBERS)
        return "{}"

    async def call_tool_json(self, token, name, arguments):
        text = await self.call_tool(token, name, arguments)
        try:
            return json.loads(text)
        except (ValueError, TypeError):
            return text

    # convenience for assertions
    def last_send(self):
        return self.sends[-1]


def _coord(tmp_path):
    return Coordinator(
        store=OrchestratorStore(str(tmp_path)),
        mcp=FakeMcp(),
        graph=build_slot_graph(MemorySaver()),
    )


def _addr_of(send):
    return send[0][0]


def _ref_in(send):
    return pipeline.extract_ref(send[1])


@pytest.mark.asyncio
async def test_full_edition_lifecycle(tmp_path):
    coord = _coord(tmp_path)
    mcp = coord._mcp  # the FakeMcp
    ED = "edition-thread-1"

    # --- 1. Director kickoff: theme + 2 slots ---------------------------
    await coord.handle(
        thread_id=ED,
        message_id="kickoff-msg",
        sender_address="unit:director",
        text="Theme: Local government\n- City budget vote\n- School board race",
        token="tok-kickoff",
    )
    # Two draft briefs, both to the staff writer, each carrying a correlation ref.
    assert len(mcp.sends) == 2
    assert all(_addr_of(s) == "agent:staffwriter" for s in mcp.sends)
    draft_refs = {_ref_in(s) for s in mcp.sends}
    assert draft_refs == {
        pipeline.correlation_id(ED, "slot-1", "draft"),
        pipeline.correlation_id(ED, "slot-2", "draft"),
    }

    # --- 2. Drive each slot through the whole pipeline ------------------
    # Expected role per stage transition after a draft reply.
    next_role = {
        "draft": "agent:factchecker",
        "fact_check": "agent:copyeditor",
        "copy_edit": "agent:audienceeditor",
    }
    for slot_id in ("slot-1", "slot-2"):
        for stage in ("draft", "fact_check", "copy_edit"):
            ref = pipeline.correlation_id(ED, slot_id, stage)
            before = len(mcp.sends)
            await coord.handle(
                thread_id=f"convo-{slot_id}-{stage}",
                message_id=f"m-{slot_id}-{stage}",
                sender_address="agent:peer",
                text=f"{stage} artifact for {slot_id} [[sv-ref:{ref}]]",
                token=f"tok-{slot_id}-{stage}",
            )
            assert len(mcp.sends) == before + 1, f"expected a delegation after {stage}"
            assert _addr_of(mcp.last_send()) == next_role[stage]
            assert _ref_in(mcp.last_send()) == pipeline.correlation_id(
                ED, slot_id, pipeline.next_stage(stage)
            )

    # --- 3. Package replies finish the slots; after the 2nd, assembly fires
    sends_before_package = len(mcp.sends)
    # slot-1 package reply → slot done, but slot-2 still open → no assembly yet
    await coord.handle(
        thread_id="c1",
        message_id="p1",
        sender_address="agent:audienceeditor",
        text=f"packaged slot-1 [[sv-ref:{pipeline.correlation_id(ED, 'slot-1', 'package')}]]",
        token="tok",
    )
    assert (
        len(mcp.sends) == sends_before_package
    )  # no new delegation; waiting on slot-2

    # slot-2 package reply → all slots packaged → assemble delegation to production
    await coord.handle(
        thread_id="c2",
        message_id="p2",
        sender_address="agent:audienceeditor",
        text=f"packaged slot-2 [[sv-ref:{pipeline.correlation_id(ED, 'slot-2', 'package')}]]",
        token="tok",
    )
    assert len(mcp.sends) == sends_before_package + 1
    assert _addr_of(mcp.last_send()) == "agent:productioneditor"
    assert _ref_in(mcp.last_send()) == pipeline.correlation_id(
        ED, "__edition__", "assemble"
    )
    assert "## City budget vote" in mcp.last_send()[1]  # assembled from packaged pieces

    # --- 4. Production returns the assembled edition → sign-off to director
    assert len(mcp.responds) == 0
    await coord.handle(
        thread_id="cprod",
        message_id="asm",
        sender_address="agent:productioneditor",
        text=f"THE ASSEMBLED EDITION [[sv-ref:{pipeline.correlation_id(ED, '__edition__', 'assemble')}]]",
        token="tok",
    )
    assert len(mcp.responds) == 1
    # respond_to continues the ORIGINAL director conversation (kickoff message id).
    assert mcp.responds[0][0] == "kickoff-msg"
    assert "sign-off" in mcp.responds[0][1].lower()

    # --- 5. Director approves on the edition thread → publish to production
    sends_before_publish = len(mcp.sends)
    await coord.handle(
        thread_id=ED,  # director replies on the edition thread (no ref needed)
        message_id="approve",
        sender_address="unit:director",
        text="Looks good — approved. Publish it.",
        token="tok",
    )
    assert len(mcp.sends) == sends_before_publish + 1
    assert _addr_of(mcp.last_send()) == "agent:productioneditor"
    assert "Publish" in mcp.last_send()[1]

    edition = coord._store.get_edition(ED)
    assert edition.phase == PHASE_PUBLISHED
    assert all(s.done for s in edition.slots.values())


@pytest.mark.asyncio
async def test_unknown_ref_is_ignored(tmp_path):
    coord = _coord(tmp_path)
    out = await coord.handle(
        thread_id="t",
        message_id="m",
        sender_address="agent:x",
        text="stray reply [[sv-ref:nobody::slot-1::draft]]",
        token="tok",
    )
    assert "unknown correlation ref" in out
    assert coord._mcp.sends == []


@pytest.mark.asyncio
async def test_director_revision_routes_back_to_production(tmp_path):
    coord = _coord(tmp_path)
    ED = "ed-rev"
    # Minimal path to SIGNOFF: single-slot edition, then drive it through.
    await coord.handle(
        thread_id=ED,
        message_id="k",
        sender_address="unit:director",
        text="One story",
        token="t",
    )
    for stage in ("draft", "fact_check", "copy_edit", "package"):
        await coord.handle(
            thread_id=f"c-{stage}",
            message_id=f"m-{stage}",
            sender_address="agent:peer",
            text=f"art [[sv-ref:{pipeline.correlation_id(ED, 'slot-1', stage)}]]",
            token="t",
        )
    # assembly fired → reply assembled
    await coord.handle(
        thread_id="casm",
        message_id="asm",
        sender_address="agent:productioneditor",
        text=f"ASSEMBLED [[sv-ref:{pipeline.correlation_id(ED, '__edition__', 'assemble')}]]",
        token="t",
    )
    sends_before = len(coord._mcp.sends)
    # Director asks for changes (not an approval).
    out = await coord.handle(
        thread_id=ED,
        message_id="notes",
        sender_address="unit:director",
        text="Please tighten the lede on the second piece.",
        token="t",
    )
    assert "revision requested" in out
    assert len(coord._mcp.sends) == sends_before + 1
    assert _addr_of(coord._mcp.last_send()) == "agent:productioneditor"
    assert _ref_in(coord._mcp.last_send()) == pipeline.correlation_id(
        ED, "__edition__", "revise"
    )


# --- pure helper tests ----------------------------------------------------

from orchestrator import coordinator as coord_mod  # noqa: E402


def test_parse_slots_bulleted_list():
    theme, slots = coord_mod.parse_slots(
        "Theme: Local government\n- City budget\n2) School board\n* Transit"
    )
    assert theme == "Local government"
    assert slots == ["City budget", "School board", "Transit"]


def test_parse_slots_no_list_is_single_slot():
    theme, slots = coord_mod.parse_slots("Just one story about the harbour")
    assert slots == ["Just one story about the harbour"]


def test_parse_slots_caps_at_max():
    text = "\n".join(f"- slot {i}" for i in range(20))
    _, slots = coord_mod.parse_slots(text)
    assert len(slots) == coord_mod.MAX_SLOTS


def test_strip_ref_removes_token_line():
    assert coord_mod.strip_ref("the artifact\n[[sv-ref:a::b::c]]") == "the artifact"


@pytest.mark.parametrize(
    "text", ["Approved!", "looks good", "go ahead and publish", "Signed off."]
)
def test_is_approval_true(text):
    assert coord_mod.is_approval(text)


@pytest.mark.parametrize(
    "text", ["Please revise the lede", "not yet", "tighten paragraph 2"]
)
def test_is_approval_false(text):
    assert not coord_mod.is_approval(text)
