"""
The edition coordinator (ADR-0066 §6, Option B) — the engine's orchestration.

The engine is the front-line A2A receiver but **not** the manager. It splits
every inbound message by its pending-responses memory (the correlation map):

* **matched** — a specialist's reply whose ``in_reply_to`` names a delegation
  the engine issued. The engine advances that slot's LangGraph pipeline
  **autonomously** — delivers the next stage's brief, or, when the slot
  finishes, marks it done and (once every slot is packaged) delegates assembly,
  then asks the director to sign off. The orchestration runs itself; Claude is
  never involved.
* **unmatched** — a director control message. The engine hands it to **Claude**
  (the SDK's claude-with-tools invocation), which *manages* the orchestration
  through this coordinator's tools (:meth:`start_edition`, :meth:`get_status`,
  :meth:`active_editions`, :meth:`cancel_edition`, :meth:`approve_edition`,
  :meth:`revise_edition`) and whose reply is the agent's response. Claude mints
  no ids itself — the engine returns the ``edition_id`` from
  :meth:`start_edition`, and Claude tracks what is running via
  :meth:`active_editions` / :meth:`get_status` (no separate memory needed).

``edition_id`` is engine-minted (a fresh UUID per :meth:`start_edition`),
decoupled from the internal SV ``thread_id`` (which is participant-set-stable
and not a per-edition key; see #3079). Multiple editions therefore coexist and
"reset" is just "mint a new id".

Deliberately free of the SDK / A2A imports (those live in ``app.py``) so the
whole flow is unit-testable with a fake MCP client, the real LangGraph engine,
and a fake ``invoke`` that stands in for Claude's tool calls.
"""

from __future__ import annotations

import logging
import uuid
from itertools import zip_longest
from typing import Awaitable, Callable

from orchestrator import mcp as mcp_tools
from orchestrator import pipeline
from orchestrator.graph import pending_interrupt
from orchestrator.reply import STRUCTURED_REPLY_CONTRACT, parse_specialist_reply
from orchestrator.state import (
    PHASE_ASSEMBLING,
    PHASE_CANCELLED,
    PHASE_DRAFTING,
    PHASE_PUBLISHED,
    PHASE_PUBLISHING,
    PHASE_SIGNOFF,
    TERMINAL_PHASES,
    Edition,
    OrchestratorStore,
)

logger = logging.getLogger("magazine-orchestrator.coordinator")

MAX_SLOTS = 6
EDITION_MARKER = "__edition__"

# How many times the engine re-delegates a step whose reply it could not parse as
# the required structured object before giving up and funnelling to the control
# plane (#3088). The first delivery is attempt 1, so this allows 2 re-sends.
MAX_REPLY_ATTEMPTS = 3

# A retry nudge re-sent to the same specialist on the same thread, so it still has
# its original brief and result in context — no need to rebuild the full brief.
RETRY_REPROMPT = (
    "Your previous reply could not be read: it was not the single JSON object "
    "this task requires. Re-send your result now as a SINGLE JSON object and "
    'nothing else — {"status": "ok", "artifact": "<the complete piece>"} if you '
    'finished, or {"status": "blocked", "reason": "<why>"} if you cannot.'
)


class DeliveryError(Exception):
    """A workflow delivery step could not complete — a role resolved to no
    directory member, or a ``send`` was not acknowledged.

    Raised by the data plane (#3086) so the failure **stops the flow** instead
    of being logged and swallowed. The control plane (``start_edition``) lets it
    surface to Claude as a tool error; the autonomous plane catches it and
    funnels it to the control-plane LLM via :meth:`Coordinator._funnel_error`.
    """

    def __init__(self, detail: str, *, role: str | None = None) -> None:
        super().__init__(detail)
        self.detail = detail
        self.role = role


# --- brief copy (pure helpers, unit-tested directly) ----------------------


def assemble_brief(edition: Edition) -> str:
    pieces = [
        f"## {slot.title}\n\n{slot.artifact or '(missing)'}"
        for slot in edition.slots.values()
    ]
    body = "\n\n".join(pieces)
    return (
        f'Assemble today\'s edition (theme: "{edition.theme}"). Build the running '
        f"order from these {len(edition.slots)} packaged pieces and return the "
        f"assembled edition:\n\n{body}" + STRUCTURED_REPLY_CONTRACT
    )


def signoff_request(assembled: str) -> str:
    # Goes to the director (a human-facing control message), not the data plane —
    # the director's free-form approve/revise reply is interpreted by Claude, so
    # this brief deliberately carries no structured-reply contract.
    return (
        "The edition is assembled and ready for your sign-off. Review it end to "
        "end against your plan and reply with your approval, or with specific "
        f"revision notes:\n\n{assembled}"
    )


def publish_brief(assembled: str) -> str:
    return (
        "The director has signed off. Publish this edition and return the "
        f"final, ready-to-deliver version:\n\n{assembled}" + STRUCTURED_REPLY_CONTRACT
    )


def revise_brief(assembled: str, notes: str) -> str:
    return (
        "The director returned revision notes on the assembled edition. Apply "
        f"them and return the revised edition.\n\nNotes:\n{notes}\n\n"
        f"Current edition:\n{assembled}" + STRUCTURED_REPLY_CONTRACT
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
        invoke: Callable[..., Awaitable[str]] | None = None,
    ) -> None:
        self._store = store
        self._mcp = mcp
        self._graph = graph
        # ADR-0066 §6 (Option B): the control plane. An unmatched (director)
        # message is handed to this callable — Claude invoked with this
        # coordinator's tools exposed over a local MCP server — whose reply is
        # the agent's response. ``None`` leaves the data plane testable
        # head-less (matched replies still advance the graph).
        self._invoke = invoke
        # ADR-0066 §2: the durable, agent-scoped MCP token — a service identity
        # for the always-on container. Seeded from IAgentContext.mcp_token at
        # init and refreshed from each message's metadata; used for every sv.*
        # call, including any NOT triggered by an inbound message.
        self._token = token
        self._role_addr: dict[str, str] = {}
        self._parent_unit_uuid: str | None = None

    async def handle(
        self,
        *,
        message_id: str,
        sender_address: str,
        text: str,
        in_reply_to: str | None = None,
        token: str = "",
    ) -> str:
        """Receive one inbound message (ADR-0066 §6, Option B).

        A reply that matches a pending delegation advances the graph
        autonomously (the data plane). Every unmatched message — a director
        control message — is handed to Claude, which manages the orchestration
        through this coordinator's tools and whose reply is the agent's
        response. The engine does **not** key on the internal ``thread_id``
        (#3079); matched messages carry their ``edition_id`` in the correlation.
        """
        self._token = token or self._token

        if in_reply_to:
            entry = self._store.pop_correlation(in_reply_to)
            if entry is not None:
                return await self._handle_correlated_reply(entry, text)

        if self._invoke is None:
            # No control-plane handler wired (head-less data-plane tests).
            return "ignored: unmatched message, no control-plane handler"
        return await self._invoke(text, sender_address=sender_address)

    # ----- orchestration tools (Claude calls these over the local MCP) -----

    async def start_edition(
        self,
        *,
        theme: str,
        slots: list[str],
        report_to: str,
        briefs: list[str] | None = None,
    ) -> str:
        """Tool: start a new edition and return its engine-minted ``edition_id``.

        Mints a fresh id (decoupled from any conversation id), creates the
        durable edition, kicks off one LangGraph pipeline per slot, delivers
        each first delegation, and returns the id for Claude to store and use in
        later ``get_status`` / ``cancel`` calls.

        ``briefs`` carries the director's complete per-story direction (angle,
        length, tone, sourcing, non-negotiables), aligned 1:1 with ``slots``.
        It is threaded into every stage so the writers honour the commission;
        without it a slot reaches the pipeline as a bare title (#3088).
        """
        edition_id = uuid.uuid4().hex
        # Pair each title with its brief BEFORE dropping blank titles, so a
        # filtered-out slot cannot misalign the remaining briefs.
        paired = [
            (str(s).strip(), str(b).strip())
            for s, b in zip_longest(slots or [], briefs or [], fillvalue="")
        ]
        paired = [(t, br) for t, br in paired if t][:MAX_SLOTS]
        slot_titles = [t for t, _ in paired]
        slot_briefs = [br for _, br in paired]
        if not slot_titles:
            slot_titles = [str(theme).strip() or "Untitled"]
            slot_briefs = [""]
        edition = self._store.create_edition(
            edition_id=edition_id,
            theme=str(theme).strip() or "Untitled edition",
            slot_titles=slot_titles,
            slot_briefs=slot_briefs,
            report_to=report_to,
            first_stage=pipeline.SLOT_STAGES[0],
        )
        try:
            for slot_id in edition.slots:
                cfg = self._slot_cfg(edition_id, slot_id)
                result = self._graph.invoke(self._initial_state(edition, slot_id), cfg)
                delegation = pending_interrupt(result)
                if delegation is not None:
                    await self._deliver(
                        delegation, edition_id=edition_id, slot_id=slot_id
                    )
        except DeliveryError as exc:
            # The flow stops (#3086): cancel the half-started edition and surface
            # the failure to the caller — Claude — as a tool error, so it tells
            # the director the edition cannot run rather than reporting a live
            # pipeline.
            edition.phase = PHASE_CANCELLED
            self._store.save_edition(edition)
            logger.error("Edition %s could not start: %s", edition_id, exc.detail)
            raise DeliveryError(
                f"The edition could not be started: {exc.detail} "
                "It has been cancelled; tell the director it cannot run until a "
                "team member fills that role.",
                role=exc.role,
            ) from exc
        logger.info(
            "Edition %s started with %d slot(s)", edition_id, len(edition.slots)
        )
        return edition_id

    def get_status(self, edition_id: str) -> dict:
        """Tool: the live status of one edition (phase + per-slot stage)."""
        edition = self._store.get_edition(edition_id)
        if edition is None:
            return {"found": False, "edition_id": edition_id}
        return {
            "found": True,
            "edition_id": edition_id,
            "theme": edition.theme,
            "phase": edition.phase,
            "running": edition.phase not in TERMINAL_PHASES,
            "slots": [
                {
                    "title": s.title,
                    "stage": "done" if s.done else s.stage,
                    "done": s.done,
                }
                for s in edition.slots.values()
            ],
        }

    def active_editions(self) -> list[dict]:
        """Tool: editions still running. Empty when nothing is in motion — how
        Claude answers "what's your progress" before any edition is kicked off."""
        return [
            {"edition_id": e.edition_id, "theme": e.theme, "phase": e.phase}
            for e in self._store.list_editions()
            if e.phase not in TERMINAL_PHASES
        ]

    async def cancel_edition(self, edition_id: str) -> dict:
        """Tool: cancel a running edition. Marks it cancelled; its correlations
        stay so any late specialist reply resolves to a terminal edition and is
        ignored rather than leaking to the control plane."""
        edition = self._store.get_edition(edition_id)
        if edition is None:
            return {"ok": False, "reason": "unknown edition", "edition_id": edition_id}
        if edition.phase in TERMINAL_PHASES:
            return {
                "ok": False,
                "reason": f"already {edition.phase}",
                "edition_id": edition_id,
            }
        edition.phase = PHASE_CANCELLED
        self._store.save_edition(edition)
        logger.info("Edition %s cancelled", edition_id)
        return {"ok": True, "edition_id": edition_id, "phase": PHASE_CANCELLED}

    async def approve_edition(self, edition_id: str) -> dict:
        """Tool: the director approved — publish, then the engine delivers the
        final edition to the reader so their response re-enters the loop (#3088)."""
        edition = self._store.get_edition(edition_id)
        if edition is None:
            return {"ok": False, "reason": "unknown edition", "edition_id": edition_id}
        if edition.phase != PHASE_SIGNOFF:
            return {"ok": False, "reason": f"not awaiting sign-off ({edition.phase})"}
        production = await self._role_address("production-editor")
        if production is None:
            return {"ok": False, "reason": "no production-editor in directory"}
        edition.phase = PHASE_PUBLISHING
        self._store.save_edition(edition)
        message_id = await mcp_tools.send_message(
            self._mcp,
            self._token,
            [production],
            publish_brief(edition.assembled or ""),
            reason="director approved — publish",
        )
        if message_id:
            self._store.put_correlation(
                message_id,
                edition_id=edition_id,
                slot_id=EDITION_MARKER,
                stage="publish",
                role="production-editor",
                attempt=1,
            )
        return {"ok": True, "edition_id": edition_id, "phase": PHASE_PUBLISHING}

    async def revise_edition(self, edition_id: str, notes: str) -> dict:
        """Tool: the director (at sign-off) or the reader (after publish) wants
        changes — re-open the edition and send the notes to production."""
        edition = self._store.get_edition(edition_id)
        if edition is None:
            return {"ok": False, "reason": "unknown edition", "edition_id": edition_id}
        if edition.phase not in (PHASE_SIGNOFF, PHASE_PUBLISHED):
            return {
                "ok": False,
                "reason": f"not at sign-off or published ({edition.phase})",
            }
        production = await self._role_address("production-editor")
        if production is None:
            return {"ok": False, "reason": "no production-editor in directory"}
        message_id = await mcp_tools.send_message(
            self._mcp,
            self._token,
            [production],
            revise_brief(edition.assembled or "", (notes or "").strip()),
            reason="revision requested",
        )
        if message_id:
            self._store.put_correlation(
                message_id,
                edition_id=edition_id,
                slot_id=EDITION_MARKER,
                stage="revise",
                role="production-editor",
                attempt=1,
            )
        edition.phase = PHASE_ASSEMBLING
        self._store.save_edition(edition)
        return {"ok": True, "edition_id": edition_id, "phase": PHASE_ASSEMBLING}

    # ----- correlated replies (the autonomous data plane) -----

    async def _handle_correlated_reply(self, entry: dict, text: str) -> str:
        edition_id = entry["edition_id"]
        slot_id = entry["slot_id"]

        edition = self._store.get_edition(edition_id)
        if edition is None:
            return "ignored: reply for unknown edition"
        if edition.phase in TERMINAL_PHASES:
            # Cancelled (or already published) mid-flight — a late reply is moot.
            return f"ignored: edition {edition.phase}"

        # Off-golden-path gate (#3088): the reply must be the agreed structured
        # object. An unparseable reply is re-delegated a bounded number of times;
        # a `blocked` reply — or exhausted retries — leaves the autonomous path
        # and funnels to the control-plane LLM, rather than folding prose as the
        # artifact (which once carried a fact-check report, sans article, onward).
        reply = parse_specialist_reply(text)
        if reply is None:
            return await self._retry_or_escalate(edition, entry)
        if not reply.ok:
            return await self._funnel_error(
                edition,
                f"{entry.get('role') or 'A specialist'} could not complete the "
                f"{entry.get('stage') or 'current'} step: "
                f"{reply.reason or 'no reason given'}.",
            )

        artifact = reply.artifact.strip()
        if slot_id == EDITION_MARKER:
            return await self._handle_edition_reply(edition, entry, artifact)
        if slot_id not in edition.slots:
            return "ignored: reply for unknown slot"
        return await self._advance_slot(edition, entry, artifact)

    async def _advance_slot(self, edition: Edition, entry: dict, artifact: str) -> str:
        edition_id = edition.edition_id
        slot_id = entry["slot_id"]
        cfg = self._slot_cfg(edition_id, slot_id)
        result = self._graph.invoke(_resume(artifact), cfg)
        delegation = pending_interrupt(result)
        slot = edition.slots[slot_id]
        slot.artifact = artifact

        if delegation is not None:
            slot.stage = delegation["stage"]
            self._store.save_edition(edition)
            try:
                await self._deliver(delegation, edition_id=edition_id, slot_id=slot_id)
            except DeliveryError as exc:
                # Autonomous plane, no Claude in the loop: stop and funnel the
                # error to the control-plane LLM (#3086).
                return await self._funnel_error(edition, exc.detail)
            return f"slot {slot_id} advanced to {delegation['stage']}"

        slot.artifact = result.get("artifact", artifact)
        slot.stage = "done"
        slot.done = True
        self._store.save_edition(edition)
        if edition.all_slots_done() and edition.phase == PHASE_DRAFTING:
            try:
                await self._start_assembly(edition)
            except DeliveryError as exc:
                return await self._funnel_error(edition, exc.detail)
            return f"slot {slot_id} packaged; edition assembling"
        return f"slot {slot_id} packaged"

    async def _retry_or_escalate(self, edition: Edition, entry: dict) -> str:
        """A correlated reply was not the required structured object (#3088).

        Re-delegate the same step to the same specialist (which still has its
        brief and its own result in context, on the shared thread) up to
        :data:`MAX_REPLY_ATTEMPTS`; once exhausted — or if the role can no longer
        be reached — funnel to the control-plane LLM like any other failure.
        """
        attempt = int(entry.get("attempt", 1))
        role = entry.get("role") or ""
        stage = entry.get("stage") or "current"
        if attempt >= MAX_REPLY_ATTEMPTS or not role:
            return await self._funnel_error(
                edition,
                f"{role or 'A specialist'} did not return a usable structured "
                f"reply for the {stage} step after {attempt} attempt(s).",
            )
        address = await self._role_address(role)
        if address is None:
            return await self._funnel_error(
                edition,
                f"No team member holds the role '{role}' to retry the {stage} step.",
            )
        message_id = await mcp_tools.send_message(
            self._mcp,
            self._token,
            [address],
            RETRY_REPROMPT,
            reason=f"retry {stage}: structured reply required",
        )
        if not message_id:
            return await self._funnel_error(
                edition,
                f"The {stage} retry to '{role}' was not acknowledged.",
            )
        self._store.put_correlation(
            message_id,
            edition_id=edition.edition_id,
            slot_id=entry["slot_id"],
            stage=stage,
            role=role,
            attempt=attempt + 1,
        )
        logger.info(
            "Edition %s: unparseable %s reply from %s; retrying (attempt %d)",
            edition.edition_id,
            stage,
            role,
            attempt + 1,
        )
        return (
            f"{stage}: unparseable reply from {role}; retrying (attempt {attempt + 1})"
        )

    async def _handle_edition_reply(
        self, edition: Edition, entry: dict, artifact: str
    ) -> str:
        # The production editor's reply to the publish step (#3088): deliver the
        # final edition to the reader THROUGH the engine, so the reader's reply
        # re-enters the control plane instead of dead-ending at the producer.
        if entry.get("stage") == "publish":
            return await self._handle_publish_reply(edition, artifact)

        # Assembly / revise reply → bring it to the director for sign-off.
        edition.assembled = artifact
        edition.phase = PHASE_SIGNOFF
        self._store.save_edition(edition)
        # ADR-0066 §6 Option B: the engine reaches the director with a NEW
        # message to report_to — it has no inbound director message to
        # respond_to (Claude mediated the kickoff). The director's approve /
        # revise reply lands unmatched and routes to Claude, which calls
        # approve_edition / revise_edition.
        if edition.report_to:
            await mcp_tools.send_message(
                self._mcp,
                self._token,
                [edition.report_to],
                signoff_request(artifact),
                reason="edition assembled — requesting sign-off",
            )
        return "edition assembled; sent to director for sign-off"

    async def _handle_publish_reply(self, edition: Edition, final: str) -> str:
        """Deliver the published edition to the reader and close the loop (#3088).

        The engine — not the production editor — sends the final edition to the
        human, so the reader's approval-or-not lands back as an *unmatched*
        message and routes to the control-plane LLM (Claude), which decides
        whether the edition is accepted or must be re-opened for revision.
        """
        edition.assembled = final
        edition.phase = PHASE_PUBLISHED
        self._store.save_edition(edition)
        reader = await self._reader_address(edition)
        if reader is None:
            logger.warning(
                "Edition %s published but no reader address to deliver to.",
                edition.edition_id,
            )
            return "edition published; no reader to deliver to"
        await mcp_tools.send_message(
            self._mcp,
            self._token,
            [reader],
            (
                "Today's edition is published. Here it is — reply with your "
                f"approval, or tell us what to change:\n\n{final}"
            ),
            reason="published edition delivered to the reader",
        )
        return "edition published and delivered to the reader"

    async def _start_assembly(self, edition: Edition) -> None:
        edition.phase = PHASE_ASSEMBLING
        self._store.save_edition(edition)
        production = await self._role_address("production-editor")
        if production is None:
            raise DeliveryError(
                "No team member holds the role 'production-editor', so the "
                "edition could not be sent for assembly.",
                role="production-editor",
            )
        message_id = await mcp_tools.send_message(
            self._mcp,
            self._token,
            [production],
            assemble_brief(edition),
            reason="all slots packaged — assemble",
        )
        if not message_id:
            raise DeliveryError(
                "The assembly brief to 'production-editor' was not acknowledged "
                "(no message id returned).",
                role="production-editor",
            )
        self._store.put_correlation(
            message_id,
            edition_id=edition.edition_id,
            slot_id=EDITION_MARKER,
            stage="assemble",
            role="production-editor",
            attempt=1,
        )

    async def _funnel_error(self, edition: Edition, detail: str) -> str:
        """A data-plane step failed with no Claude in the loop (#3086).

        Stop the flow and funnel the error to the control-plane LLM — the
        Managing Editor — exactly like an unmatched inbound message, so it
        decides how to respond. Its reply is carried to the director
        (``report_to``) by the engine, because the inbound that triggered this
        was a specialist's reply, not a director message.
        """
        logger.error("Pipeline error on edition %s: %s", edition.edition_id, detail)
        if self._invoke is None:
            # Head-less data-plane tests: no control plane to funnel to.
            return f"pipeline error: {detail}"
        brief = (
            f'A pipeline error stopped the edition "{edition.theme}" '
            f"(edition_id {edition.edition_id}): {detail}\n\n"
            "The autonomous pipeline has halted — do not treat the edition as "
            "still running. Decide how to handle this and reply with what the "
            "director should be told; your reply is delivered to them. You may "
            "also cancel the edition with your tools."
        )
        reply = (
            await self._invoke(brief, sender_address="system://pipeline-error") or ""
        ).strip()
        if edition.report_to and reply:
            await mcp_tools.send_message(
                self._mcp,
                self._token,
                [edition.report_to],
                reply,
                reason="pipeline error — managing editor notifying director",
            )
        return reply or f"pipeline error: {detail}"

    # ----- delivery + directory -----

    async def _deliver(
        self, delegation: dict, *, edition_id: str, slot_id: str
    ) -> None:
        role = delegation["role"]
        stage = delegation["stage"]
        address = await self._role_address(role)
        if address is None:
            raise DeliveryError(
                f"No team member holds the role '{role}', so the {stage} brief "
                f"could not be delivered.",
                role=role,
            )
        # ADR-0066 §5: record the id `send` returns; the specialist's reply names
        # it as `in_reply_to`, which routes the reply back to this slot+stage.
        message_id = await mcp_tools.send_message(
            self._mcp,
            self._token,
            [address],
            delegation["body"],
            reason=f"{stage} brief for {slot_id}",
        )
        if not message_id:
            raise DeliveryError(
                f"The {stage} brief to '{role}' was not acknowledged (no message "
                f"id returned), so the reply could not be correlated.",
                role=role,
            )
        self._store.put_correlation(
            message_id,
            edition_id=edition_id,
            slot_id=slot_id,
            stage=stage,
            role=role,
            attempt=1,
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

    async def _reader_address(self, edition: Edition) -> str | None:
        """The human the published edition is delivered to — the unit's human
        member (#3088). Resolved from the same membership list as the roles; the
        engine sends to them directly so their reply re-enters the control plane.
        Falls back to ``report_to`` (the director) when the unit has no human."""
        if self._parent_unit_uuid is None:
            me = await mcp_tools.get_self(self._mcp, self._token)
            parents = me.get("parent_uuids") or []
            self._parent_unit_uuid = parents[0] if parents else None
        if self._parent_unit_uuid:
            members = await mcp_tools.list_members(
                self._mcp, self._token, self._parent_unit_uuid
            )
            for member in members:
                address = member.get("address")
                if not isinstance(address, str) or not address:
                    continue
                kind = member.get("kind") or address.split(":", 1)[0]
                if kind == "human":
                    return address
        return edition.report_to or None

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
            "slot_brief": slot.brief,
            "theme": edition.theme,
            "artifact": None,
            "stages_done": [],
        }
