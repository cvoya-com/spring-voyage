"""End-to-end orchestration test: the real LangGraph engine + a fake MCP client.

Drives a full two-slot edition from the director's kickoff, through every
specialist stage of every slot, through assembly, to the director's sign-off and
the publish hand-off. Correlation is platform-native (ADR-0066 §5): a brief's id
is what `send` returns, and a reply names it via `in_reply_to` — no echoed token.

Skipped when LangGraph is not installed.
"""

from __future__ import annotations

import pytest

pytest.importorskip("langgraph")

from langgraph.checkpoint.memory import MemorySaver  # noqa: E402

from orchestrator import mcp as mcp_tools  # noqa: E402
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
    """Records sends/responds and serves a canned directory. send/respond_to
    return an ack carrying the created message's id (msg-N / resp-N), which the
    orchestrator correlates against a later reply's in_reply_to."""

    def __init__(self):
        self.sends: list[tuple[list[str], str, str | None]] = []
        self.responds: list[tuple[str, str]] = []

    async def call_tool_json(self, token, name, arguments):
        if name == mcp_tools.SEND_TOOL:
            self.sends.append(
                (arguments["recipients"], arguments["message"], arguments.get("reason"))
            )
            return {"messageId": f"msg-{len(self.sends)}", "deliveries": []}
        if name == mcp_tools.RESPOND_TO_TOOL:
            self.responds.append((arguments["message_id"], arguments["message"]))
            return {"messageId": f"resp-{len(self.responds)}", "deliveries": []}
        if name == mcp_tools.GET_SELF_TOOL:
            return {"parent_uuids": ["unit-1"]}
        if name == mcp_tools.LIST_MEMBERS_TOOL:
            return _MEMBERS
        return {}

    def last_send(self):
        return self.sends[-1]


def _coord(tmp_path):
    return Coordinator(
        store=OrchestratorStore(str(tmp_path)),
        mcp=FakeMcp(),
        graph=build_slot_graph(MemorySaver()),
        token="durable-tok",
    )


def _addr(send):
    return send[0][0]


async def _reply(coord, in_reply_to, *, text="artifact", sender="agent:peer"):
    """Simulate a specialist replying to the brief identified by *in_reply_to*."""
    return await coord.handle(
        thread_id=f"c-{in_reply_to}",
        message_id="m",
        sender_address=sender,
        text=text,
        in_reply_to=in_reply_to,
    )


@pytest.mark.asyncio
async def test_full_edition_lifecycle(tmp_path):
    coord = _coord(tmp_path)
    mcp = coord._mcp
    ED = "edition-thread-1"

    # --- 1. Director kickoff: theme + 2 slots → 2 draft briefs to the writer.
    await coord.handle(
        thread_id=ED,
        message_id="kickoff-msg",
        sender_address="unit:director",
        text="Theme: Local government\n- City budget vote\n- School board race",
    )
    assert len(mcp.sends) == 2
    assert all(_addr(s) == "agent:staffwriter" for s in mcp.sends)
    # No echoed token in any brief — correlation is platform-native.
    assert all("sv-ref" not in s[1] for s in mcp.sends)
    slot1_draft, slot2_draft = "msg-1", "msg-2"

    # --- 2. Drive slot-1 by replying to each brief's id; assert the next role.
    await _reply(coord, slot1_draft)
    assert _addr(mcp.last_send()) == "agent:factchecker"
    await _reply(coord, f"msg-{len(mcp.sends)}")
    assert _addr(mcp.last_send()) == "agent:copyeditor"
    await _reply(coord, f"msg-{len(mcp.sends)}")
    assert _addr(mcp.last_send()) == "agent:audienceeditor"
    # package reply finishes slot-1; slot-2 still open → no new delegation.
    before = len(mcp.sends)
    await _reply(coord, f"msg-{len(mcp.sends)}")
    assert len(mcp.sends) == before

    # --- 3. Drive slot-2; its package reply triggers assembly.
    await _reply(coord, slot2_draft)  # → fact-checker
    await _reply(coord, f"msg-{len(mcp.sends)}")  # → copy-editor
    await _reply(coord, f"msg-{len(mcp.sends)}")  # → audience-editor
    before = len(mcp.sends)
    await _reply(coord, f"msg-{len(mcp.sends)}")  # package → all done → assemble
    assert len(mcp.sends) == before + 1
    assert _addr(mcp.last_send()) == "agent:productioneditor"
    assert "## City budget vote" in mcp.last_send()[1]
    assemble_id = f"msg-{len(mcp.sends)}"

    # --- 4. Production returns the assembled edition → sign-off to the director.
    assert len(mcp.responds) == 0
    await _reply(
        coord,
        assemble_id,
        text="THE ASSEMBLED EDITION",
        sender="agent:productioneditor",
    )
    assert len(mcp.responds) == 1
    # respond_to continues the ORIGINAL director conversation (kickoff message).
    assert mcp.responds[0][0] == "kickoff-msg"
    assert "sign-off" in mcp.responds[0][1].lower()

    # --- 5. Director approves on the edition thread. Its in_reply_to (resp-1)
    #        is not a tracked delegation, so it routes by edition phase.
    before = len(mcp.sends)
    await coord.handle(
        thread_id=ED,
        message_id="approve",
        sender_address="unit:director",
        text="Looks good — approved. Publish it.",
        in_reply_to="resp-1",
    )
    assert len(mcp.sends) == before + 1
    assert _addr(mcp.last_send()) == "agent:productioneditor"
    assert "Publish" in mcp.last_send()[1]

    edition = coord._store.get_edition(ED)
    assert edition.phase == PHASE_PUBLISHED
    assert all(s.done for s in edition.slots.values())


@pytest.mark.asyncio
async def test_unknown_in_reply_to_on_new_thread_is_ignored(tmp_path):
    coord = _coord(tmp_path)
    out = await coord.handle(
        thread_id="stray",
        message_id="m",
        sender_address="agent:x",
        text="a stray reply",
        in_reply_to="msg-does-not-exist",
    )
    assert "ignored" in out
    assert coord._mcp.sends == []  # a stray reply must not start an edition


@pytest.mark.asyncio
async def test_director_revision_routes_back_to_production(tmp_path):
    coord = _coord(tmp_path)
    mcp = coord._mcp
    ED = "ed-rev"
    # Single-slot edition (no bulleted list → one slot).
    await coord.handle(
        thread_id=ED, message_id="k", sender_address="unit:director", text="One story"
    )
    nxt = "msg-1"
    for _ in range(3):  # draft → fact_check → copy_edit → package briefs
        await _reply(coord, nxt)
        nxt = f"msg-{len(mcp.sends)}"
    before = len(mcp.sends)
    await _reply(coord, nxt)  # package reply → all done → assemble
    assert len(mcp.sends) == before + 1
    assemble_id = f"msg-{len(mcp.sends)}"
    await _reply(coord, assemble_id, text="ASSEMBLED", sender="agent:productioneditor")

    # Director returns notes (not an approval) → revise pass back to production.
    before = len(mcp.sends)
    out = await coord.handle(
        thread_id=ED,
        message_id="notes",
        sender_address="unit:director",
        text="Please tighten the lede on the second piece.",
        in_reply_to="resp-1",
    )
    assert "revision requested" in out
    assert len(mcp.sends) == before + 1
    assert _addr(mcp.last_send()) == "agent:productioneditor"


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


# --- ADR-0066: Claude intake (route-to-Claude on no pending match) ---------


def _coord_with_complete(tmp_path, complete):
    return Coordinator(
        store=OrchestratorStore(str(tmp_path)),
        mcp=FakeMcp(),
        graph=build_slot_graph(MemorySaver()),
        token="durable-tok",
        complete=complete,
    )


@pytest.mark.asyncio
async def test_kickoff_uses_claude_intake_when_wired(tmp_path):
    # A free-form brief with no bulleted list: the heuristic would make ONE
    # slot, but the wired Claude intake extracts a structured plan with three.
    calls = []

    async def fake_complete(prompt, *, system_prompt=None):
        calls.append((prompt, system_prompt))
        return (
            '{"theme": "City hall in focus", '
            '"slots": ["Budget vote", "Zoning fight", "Mayor profile"]}'
        )

    coord = _coord_with_complete(tmp_path, fake_complete)
    mcp = coord._mcp
    await coord.handle(
        thread_id="ed-claude",
        message_id="k",
        sender_address="unit:director",
        text="Do a deep issue on what's happening at city hall this month.",
    )
    # Claude saw the brief under the focused intake instruction.
    assert calls and calls[0][1] == coord_mod.INTAKE_SYSTEM_PROMPT
    # Three slots → three draft briefs (vs one from the heuristic).
    assert len(mcp.sends) == 3
    edition = coord._store.get_edition("ed-claude")
    assert edition.theme == "City hall in focus"
    assert [s.title for s in edition.slots.values()] == [
        "Budget vote",
        "Zoning fight",
        "Mayor profile",
    ]


@pytest.mark.asyncio
async def test_kickoff_falls_back_to_heuristic_when_claude_fails(tmp_path):
    async def boom(prompt, *, system_prompt=None):
        raise RuntimeError("claude exploded")

    coord = _coord_with_complete(tmp_path, boom)
    await coord.handle(
        thread_id="ed-fallback",
        message_id="k",
        sender_address="unit:director",
        text="Theme: Transit\n- Bus cuts\n- Bike lanes",
    )
    # The heuristic parse drove it: two bulleted slots, edition still created.
    assert len(coord._mcp.sends) == 2
    edition = coord._store.get_edition("ed-fallback")
    assert edition.theme == "Transit"
    assert len(edition.slots) == 2


@pytest.mark.asyncio
async def test_interpret_brief_tolerates_fenced_json(tmp_path):
    async def fenced(prompt, *, system_prompt=None):
        return 'Sure!\n```json\n{"theme": "T", "slots": ["a", "b"]}\n```\n'

    coord = _coord_with_complete(tmp_path, fenced)
    theme, slots = await coord._interpret_brief("anything")
    assert theme == "T"
    assert slots == ["a", "b"]


@pytest.mark.asyncio
async def test_interpret_brief_unusable_result_falls_back(tmp_path):
    async def empty(prompt, *, system_prompt=None):
        return '{"theme": "", "slots": []}'

    coord = _coord_with_complete(tmp_path, empty)
    _, slots = await coord._interpret_brief("One story about the docks")
    # An empty Claude result is not usable → heuristic single slot.
    assert slots == ["One story about the docks"]


@pytest.mark.asyncio
async def test_interpret_brief_none_complete_uses_heuristic(tmp_path):
    coord = _coord(tmp_path)  # no complete wired → fully deterministic
    theme, slots = await coord._interpret_brief("Theme: X\n- a\n- b")
    assert theme == "X"
    assert slots == ["a", "b"]


def test_extract_json_object_variants():
    assert coord_mod._extract_json_object('{"a": 1}') == '{"a": 1}'
    assert coord_mod._extract_json_object('prefix {"a": 1} suffix') == '{"a": 1}'
    assert coord_mod._extract_json_object('```json\n{"a": 1}\n```') == '{"a": 1}'
    assert coord_mod._extract_json_object("no json here") == "no json here"
