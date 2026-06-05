"""
The edition coordinator (ADR-0066) — the engine's orchestration logic.

Deliberately free of the SDK / A2A imports (those live in ``app.py``) so the
whole coordination flow can be unit-tested with a fake MCP client and the real
LangGraph engine, no platform required. ``Coordinator`` takes plain message
fields, not the SDK ``Message`` type.

Run loop, per inbound message:

* **kickoff** — a director message on a fresh conversation (matches no pending
  response). Interpret the natural-language brief into a theme and story slots
  — via Claude when a completion capability is wired, the heuristic parse
  otherwise — create the durable edition, start one LangGraph slot pipeline per
  slot, and deliver each first delegation (the draft brief).
* **peer reply** — a specialist's reply, matched to its brief by the reply's
  ``in_reply_to`` (ADR-0066 §5 — no echoed token). Resume that slot's graph with
  the returned artifact; deliver the next stage's delegation, or — when the slot
  finishes — mark it done and, once every slot is packaged, delegate assembly.
* **assemble reply** — the production editor's assembled edition. Bring it to
  the director for sign-off on the edition thread.
* **sign-off** — the director's approve/changes on the edition thread. On
  approval, release to production to publish; otherwise send the notes back for
  a revise pass.
"""

from __future__ import annotations

import json
import logging
import re
from typing import Awaitable, Callable

from orchestrator import mcp as mcp_tools
from orchestrator import pipeline
from orchestrator.graph import pending_interrupt
from orchestrator.state import (
    PHASE_ASSEMBLING,
    PHASE_DRAFTING,
    PHASE_PUBLISHED,
    PHASE_SIGNOFF,
    Edition,
    OrchestratorStore,
)

logger = logging.getLogger("magazine-orchestrator.coordinator")

MAX_SLOTS = 6
EDITION_MARKER = "__edition__"
_LIST_RE = re.compile(r"^(?:[-*]|\d+[.)])\s+(.*)")
_APPROVE_RE = re.compile(
    r"\b(approved?|sign(?:ed)?\s*off|signs?\s*off|looks good|ship it|go ahead|publish)\b",
    re.IGNORECASE,
)

# ADR-0066: the focused instruction handed to Claude when interpreting a
# free-form director brief into a structured edition plan. The engine owns
# orchestration; Claude owns the natural-language step. Kept narrow (extract,
# don't converse) so the result is machine-parsable.
INTAKE_SYSTEM_PROMPT = (
    "You convert a magazine editor's natural-language brief into a structured "
    "edition plan. Return ONLY a JSON object — no prose, no markdown fences — of "
    'the form {"theme": "<one short line>", "slots": ["<story slot title>", ...]}.'
    "\nRules:\n"
    '- "theme" is the overarching subject of the edition.\n'
    '- "slots" is the ordered list of distinct story pieces to commission '
    "(1-6 items).\n"
    "- If the brief names specific stories, use them as the slots.\n"
    "- If it gives only a theme with no explicit stories, propose 2-4 concrete "
    "story slots that fit it."
)


# --- pure helpers (unit-tested directly) ----------------------------------


def parse_slots(text: str) -> tuple[str, list[str]]:
    """Heuristically split a director brief into a theme + story slot titles.

    A bulleted/numbered list becomes the slots; the first line is the theme.
    A brief with no list becomes a single slot. Deterministic — the fallback
    when the Claude intake path (:meth:`Coordinator._interpret_brief`) is
    unavailable or returns nothing usable, so routing is always correct.
    """
    lines = [ln.strip() for ln in (text or "").splitlines() if ln.strip()]
    theme = lines[0] if lines else "Today's edition"
    theme = (
        re.sub(r"^(?:theme|edition)\s*:\s*", "", theme, flags=re.IGNORECASE).strip()
        or theme
    )
    slots = [m.group(1).strip() for ln in lines if (m := _LIST_RE.match(ln))]
    if not slots:
        slots = [theme]
    return theme, slots[:MAX_SLOTS]


def _extract_json_object(text: str) -> str:
    """Pull the first ``{...}`` JSON object out of an LLM response.

    Tolerates a ```json fenced block or surrounding prose so a slightly chatty
    completion still parses. Returns the input unchanged when no object is
    found (the caller's ``json.loads`` then raises and the heuristic takes
    over).
    """
    s = (text or "").strip()
    fence = re.search(r"```(?:json)?\s*(\{.*?\})\s*```", s, re.DOTALL)
    if fence:
        return fence.group(1)
    start = s.find("{")
    end = s.rfind("}")
    if start != -1 and end > start:
        return s[start : end + 1]
    return s


def is_approval(text: str) -> bool:
    return bool(_APPROVE_RE.search(text or ""))


def assemble_brief(edition: Edition) -> str:
    pieces = [
        f"## {slot.title}\n\n{slot.artifact or '(missing)'}"
        for slot in edition.slots.values()
    ]
    body = "\n\n".join(pieces)
    return (
        f'Assemble today\'s edition (theme: "{edition.theme}"). Build the running '
        f"order from these {len(edition.slots)} packaged pieces and return the "
        f"assembled edition:\n\n{body}"
    )


def signoff_request(assembled: str) -> str:
    return (
        "The edition is assembled and ready for your sign-off. Review it end to "
        "end against your plan and reply with your approval, or with specific "
        f"revision notes:\n\n{assembled}"
    )


def publish_brief(assembled: str) -> str:
    return (
        "The director has signed off. Publish this edition and deliver it to the "
        f"human publisher for the final go:\n\n{assembled}"
    )


def revise_brief(assembled: str, notes: str) -> str:
    return (
        "The director returned revision notes on the assembled edition. Apply "
        f"them and return the revised edition.\n\nNotes:\n{notes}\n\n"
        f"Current edition:\n{assembled}"
    )


def _resume(value):
    """Build a LangGraph resume command. Imported lazily so importing this
    module's pure helpers does not require LangGraph."""
    from langgraph.types import Command

    return Command(resume=value)


# --- the coordinator ------------------------------------------------------


class Coordinator:
    """Drives editions across the LangGraph slot pipelines and the directory /
    messaging tools. Holds no I/O state beyond the injected store, mcp client,
    and compiled graph; safe to rebuild from durable state at any time."""

    def __init__(
        self,
        *,
        store: OrchestratorStore,
        mcp: mcp_tools.McpClient,
        graph,
        token: str = "",
        complete: Callable[..., Awaitable[str]] | None = None,
    ) -> None:
        self._store = store
        self._mcp = mcp
        self._graph = graph
        # ADR-0066: the natural-language step. The engine routes a message that
        # matches no pending response (a fresh director brief) through this
        # injected Claude completion; ``None`` keeps the engine fully
        # deterministic (the heuristic parse), which is how the coordinator is
        # unit-tested without an LLM.
        self._complete = complete
        # ADR-0066 §2: the durable, agent-scoped MCP token — a service identity
        # for the always-on container. Seeded from IAgentContext.mcp_token at
        # init and refreshed from each message's metadata; used for every sv.*
        # call, including any NOT triggered by an inbound message (a timer or
        # background step), which a per-turn token could not authenticate.
        self._token = token
        self._role_addr: dict[str, str] = {}
        self._parent_unit_uuid: str | None = None

    async def handle(
        self,
        *,
        thread_id: str,
        message_id: str,
        sender_address: str,
        text: str,
        in_reply_to: str | None = None,
        token: str = "",
    ) -> str:
        """Route one inbound message. Returns a short diagnostic summary.

        ``in_reply_to`` is the platform-native correlation (ADR-0066 §5): when a
        specialist's reply names the brief it answers, it routes that reply to
        the right slot+stage — no echoed token. ``token`` refreshes the durable
        token if the platform rotated it; all downstream calls authenticate with
        ``self._token``.
        """
        self._token = token or self._token

        if in_reply_to:
            entry = self._store.pop_correlation(in_reply_to)
            if entry is not None:
                return await self._handle_correlated_reply(entry, text)
            # Not a tracked delegation. It may still be the director's sign-off
            # reply on the edition thread (routed by phase below), but a reply on
            # a thread with no edition is a stray — never a new edition.

        edition = self._store.get_edition(thread_id)
        if edition is None:
            if in_reply_to:
                return "ignored: reply to an untracked message"
            return await self._handle_kickoff(
                thread_id, message_id, sender_address, text
            )
        if edition.phase == PHASE_SIGNOFF:
            return await self._handle_signoff(edition, text)
        return "note acknowledged (edition in progress)"

    # ----- kickoff -----

    async def _interpret_brief(self, text: str) -> tuple[str, list[str]]:
        """Turn a free-form director brief into ``(theme, slot_titles)``.

        ADR-0066: a message that matched no pending response is a fresh brief in
        natural language. Route it through Claude when a completion capability
        is wired (the engine owns orchestration; Claude owns the NL step); fall
        back to the deterministic :func:`parse_slots` heuristic when Claude is
        unavailable or returns nothing usable, so kickoff is always correct.
        """
        if self._complete is None:
            return parse_slots(text)
        try:
            raw = await self._complete(text, system_prompt=INTAKE_SYSTEM_PROMPT)
            spec = json.loads(_extract_json_object(raw))
            theme = str(spec.get("theme", "")).strip()
            slots = [
                str(s).strip() for s in (spec.get("slots") or []) if str(s).strip()
            ]
            slots = slots[:MAX_SLOTS]
            if theme and slots:
                logger.info("Claude intake parsed %d slot(s) for the brief", len(slots))
                return theme, slots
            logger.warning(
                "Claude intake returned no usable theme/slots; using heuristic parse"
            )
        except Exception:  # noqa: BLE001 — never fail intake on the LLM path.
            logger.warning(
                "Claude intake failed; falling back to heuristic parse", exc_info=True
            )
        return parse_slots(text)

    async def _handle_kickoff(
        self, thread_id: str, message_id: str, sender_address: str, text: str
    ) -> str:
        theme, slot_titles = await self._interpret_brief(text)
        edition = self._store.create_edition(
            edition_id=thread_id,
            theme=theme,
            slot_titles=slot_titles,
            report_to=sender_address,
            origin_message_id=message_id,
            first_stage=pipeline.SLOT_STAGES[0],
        )
        for slot_id in edition.slots:
            cfg = self._slot_cfg(thread_id, slot_id)
            result = self._graph.invoke(self._initial_state(edition, slot_id), cfg)
            delegation = pending_interrupt(result)
            if delegation is not None:
                await self._deliver(delegation, edition_id=thread_id, slot_id=slot_id)
        logger.info("Edition %s started with %d slots", thread_id, len(edition.slots))
        return f"edition started: {len(edition.slots)} slot(s) assigned"

    # ----- correlated replies (specialist stages + assemble/revise) -----

    async def _handle_correlated_reply(self, entry: dict, text: str) -> str:
        edition_id = entry["edition_id"]
        slot_id = entry["slot_id"]
        artifact = (text or "").strip()

        if slot_id == EDITION_MARKER:
            return await self._handle_edition_reply(
                edition_id, entry["stage"], artifact
            )

        edition = self._store.get_edition(edition_id)
        if edition is None or slot_id not in edition.slots:
            return "ignored: reply for unknown edition/slot"

        cfg = self._slot_cfg(edition_id, slot_id)
        result = self._graph.invoke(_resume(artifact), cfg)
        delegation = pending_interrupt(result)
        slot = edition.slots[slot_id]
        slot.artifact = artifact

        if delegation is not None:
            slot.stage = delegation["stage"]
            self._store.save_edition(edition)
            await self._deliver(delegation, edition_id=edition_id, slot_id=slot_id)
            return f"slot {slot_id} advanced to {delegation['stage']}"

        slot.artifact = result.get("artifact", artifact)
        slot.stage = "done"
        slot.done = True
        self._store.save_edition(edition)
        if edition.all_slots_done() and edition.phase == PHASE_DRAFTING:
            await self._start_assembly(edition)
            return f"slot {slot_id} packaged; edition assembling"
        return f"slot {slot_id} packaged"

    async def _handle_edition_reply(
        self, edition_id: str, stage: str, artifact: str
    ) -> str:
        edition = self._store.get_edition(edition_id)
        if edition is None:
            return "ignored: reply for unknown edition"
        edition.assembled = artifact
        edition.phase = PHASE_SIGNOFF
        self._store.save_edition(edition)
        await mcp_tools.respond_to(
            self._mcp,
            self._token,
            edition.origin_message_id,
            signoff_request(artifact),
            reason="edition assembled — requesting sign-off",
        )
        return "edition assembled; sent to director for sign-off"

    # ----- director sign-off -----

    async def _handle_signoff(self, edition: Edition, text: str) -> str:
        production = await self._role_address("production-editor")
        if production is None:
            return "error: no production-editor in directory"

        if is_approval(text):
            edition.phase = PHASE_PUBLISHED
            self._store.save_edition(edition)
            await mcp_tools.send_message(
                self._mcp,
                self._token,
                [production],
                publish_brief(edition.assembled or ""),
                reason="director approved — publish",
            )
            return "approved; released to production to publish"

        message_id = await mcp_tools.send_message(
            self._mcp,
            self._token,
            [production],
            revise_brief(edition.assembled or "", (text or "").strip()),
            reason="director requested revisions",
        )
        if message_id:
            self._store.put_correlation(
                message_id,
                edition_id=edition.edition_id,
                slot_id=EDITION_MARKER,
                stage="revise",
            )
        edition.phase = PHASE_ASSEMBLING
        self._store.save_edition(edition)
        return "revision requested; sent to production"

    # ----- assembly -----

    async def _start_assembly(self, edition: Edition) -> None:
        edition.phase = PHASE_ASSEMBLING
        self._store.save_edition(edition)
        production = await self._role_address("production-editor")
        if production is None:
            logger.error(
                "Cannot assemble edition %s: no production-editor", edition.edition_id
            )
            return
        message_id = await mcp_tools.send_message(
            self._mcp,
            self._token,
            [production],
            assemble_brief(edition),
            reason="all slots packaged — assemble",
        )
        if message_id:
            self._store.put_correlation(
                message_id,
                edition_id=edition.edition_id,
                slot_id=EDITION_MARKER,
                stage="assemble",
            )

    # ----- delivery + directory -----

    async def _deliver(
        self, delegation: dict, *, edition_id: str, slot_id: str
    ) -> None:
        address = await self._role_address(delegation["role"])
        if address is None:
            logger.error(
                "No directory member holds role %s; cannot deliver", delegation["role"]
            )
            return
        # ADR-0066 §5: record the id `send` returns; the specialist's reply names
        # it as `in_reply_to`, which routes the reply back to this slot+stage.
        message_id = await mcp_tools.send_message(
            self._mcp,
            self._token,
            [address],
            delegation["body"],
            reason=f"{delegation['stage']} brief for {slot_id}",
        )
        if message_id:
            self._store.put_correlation(
                message_id,
                edition_id=edition_id,
                slot_id=slot_id,
                stage=delegation["stage"],
            )
        else:
            logger.error(
                "send returned no message id for %s %s; reply cannot be correlated",
                slot_id,
                delegation["stage"],
            )

    async def _role_address(self, role: str) -> str | None:
        if role in self._role_addr:
            return self._role_addr[role]
        if self._parent_unit_uuid is None:
            me = await mcp_tools.get_self(self._mcp, self._token)
            parents = me.get("parent_uuids") or []
            self._parent_unit_uuid = parents[0] if parents else None
        if not self._parent_unit_uuid:
            return None
        members = await mcp_tools.list_members(
            self._mcp, self._token, self._parent_unit_uuid
        )
        for member in members:
            address = member.get("address")
            if not isinstance(address, str) or not address:
                continue
            for member_role in member.get("roles") or []:
                self._role_addr.setdefault(member_role, address)
        return self._role_addr.get(role)

    # ----- graph plumbing -----

    @staticmethod
    def _slot_cfg(edition_id: str, slot_id: str) -> dict:
        return {"configurable": {"thread_id": f"{edition_id}:{slot_id}"}}

    @staticmethod
    def _initial_state(edition: Edition, slot_id: str) -> dict:
        slot = edition.slots[slot_id]
        return {
            "edition_id": edition.edition_id,
            "slot_id": slot_id,
            "slot_title": slot.title,
            "theme": edition.theme,
            "artifact": None,
            "stages_done": [],
        }
