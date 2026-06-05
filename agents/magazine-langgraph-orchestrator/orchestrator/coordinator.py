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
from typing import Awaitable, Callable

from orchestrator import mcp as mcp_tools
from orchestrator import pipeline
from orchestrator.graph import pending_interrupt
from orchestrator.state import (
    PHASE_ASSEMBLING,
    PHASE_CANCELLED,
    PHASE_DRAFTING,
    PHASE_PUBLISHED,
    PHASE_SIGNOFF,
    TERMINAL_PHASES,
    Edition,
    OrchestratorStore,
)

logger = logging.getLogger("magazine-orchestrator.coordinator")

MAX_SLOTS = 6
EDITION_MARKER = "__edition__"


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
        self, *, theme: str, slots: list[str], report_to: str
    ) -> str:
        """Tool: start a new edition and return its engine-minted ``edition_id``.

        Mints a fresh id (decoupled from any conversation id), creates the
        durable edition, kicks off one LangGraph pipeline per slot, delivers
        each first delegation, and returns the id for Claude to store and use in
        later ``get_status`` / ``cancel`` calls.
        """
        edition_id = uuid.uuid4().hex
        slot_titles = [str(s).strip() for s in (slots or []) if str(s).strip()][
            :MAX_SLOTS
        ]
        if not slot_titles:
            slot_titles = [str(theme).strip() or "Untitled"]
        edition = self._store.create_edition(
            edition_id=edition_id,
            theme=str(theme).strip() or "Untitled edition",
            slot_titles=slot_titles,
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
        """Tool: the director approved — release to production to publish."""
        edition = self._store.get_edition(edition_id)
        if edition is None:
            return {"ok": False, "reason": "unknown edition", "edition_id": edition_id}
        if edition.phase != PHASE_SIGNOFF:
            return {"ok": False, "reason": f"not awaiting sign-off ({edition.phase})"}
        production = await self._role_address("production-editor")
        if production is None:
            return {"ok": False, "reason": "no production-editor in directory"}
        edition.phase = PHASE_PUBLISHED
        self._store.save_edition(edition)
        await mcp_tools.send_message(
            self._mcp,
            self._token,
            [production],
            publish_brief(edition.assembled or ""),
            reason="director approved — publish",
        )
        return {"ok": True, "edition_id": edition_id, "phase": PHASE_PUBLISHED}

    async def revise_edition(self, edition_id: str, notes: str) -> dict:
        """Tool: the director wants changes — send the notes to production."""
        edition = self._store.get_edition(edition_id)
        if edition is None:
            return {"ok": False, "reason": "unknown edition", "edition_id": edition_id}
        if edition.phase != PHASE_SIGNOFF:
            return {"ok": False, "reason": f"not awaiting sign-off ({edition.phase})"}
        production = await self._role_address("production-editor")
        if production is None:
            return {"ok": False, "reason": "no production-editor in directory"}
        message_id = await mcp_tools.send_message(
            self._mcp,
            self._token,
            [production],
            revise_brief(edition.assembled or "", (notes or "").strip()),
            reason="director requested revisions",
        )
        if message_id:
            self._store.put_correlation(
                message_id,
                edition_id=edition_id,
                slot_id=EDITION_MARKER,
                stage="revise",
            )
        edition.phase = PHASE_ASSEMBLING
        self._store.save_edition(edition)
        return {"ok": True, "edition_id": edition_id, "phase": PHASE_ASSEMBLING}

    # ----- correlated replies (the autonomous data plane) -----

    async def _handle_correlated_reply(self, entry: dict, text: str) -> str:
        edition_id = entry["edition_id"]
        slot_id = entry["slot_id"]
        artifact = (text or "").strip()

        edition = self._store.get_edition(edition_id)
        if edition is None:
            return "ignored: reply for unknown edition"
        if edition.phase in TERMINAL_PHASES:
            # Cancelled (or already published) mid-flight — a late reply is moot.
            return f"ignored: edition {edition.phase}"

        if slot_id == EDITION_MARKER:
            return await self._handle_edition_reply(edition, artifact)

        if slot_id not in edition.slots:
            return "ignored: reply for unknown slot"

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

    async def _handle_edition_reply(self, edition: Edition, artifact: str) -> str:
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
