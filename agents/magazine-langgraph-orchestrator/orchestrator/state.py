"""
Durable orchestrator state on the workspace volume (ADR-0066 §4).

The engine process is always-on but the platform owns scheduling: it runs only
when a message is dispatched. Between turns, the edition picture and the
correlation map must survive in durable storage. The per-agent workspace volume
(``$SPRING_WORKSPACE_PATH``) is that store — it survives container crash,
health-restart, redeploy, and resumable stop, and is reclaimed only on agent
delete.

This module is pure (stdlib only) so it is unit-testable against a tmp dir.
LangGraph keeps its own checkpoint (the per-slot graph state) in a SQLite file
alongside these JSON files; this store holds the edition-level bookkeeping the
graph does not: which slots exist, the latest artifact per slot, the edition
lifecycle phase, and the ref→(slot,stage) correlation map.
"""

from __future__ import annotations

import json
import os
import tempfile
import uuid
from dataclasses import asdict, dataclass, field
from pathlib import Path


def _canonical_ref(ref: str) -> str:
    """Canonicalize a correlation ref (a message-id GUID) to dashless lowercase
    hex, so correlation keys on the message's *identity* rather than its textual
    format. ``put_correlation`` keys by the id a ``send`` ack returns; later
    ``pop_correlation`` keys by an inbound reply's ``in_reply_to``. Those two
    surfaces may format the same Guid differently (dashed vs no-dash) — keying on
    the raw string then silently never matches and the pipeline stalls (#3088).
    Non-Guid refs (never expected) fall through unchanged but lowercased."""
    try:
        return uuid.UUID(str(ref)).hex
    except (ValueError, AttributeError, TypeError):
        return str(ref).strip().lower()


# Edition lifecycle phases.
PHASE_DRAFTING = "drafting"  # one or more slots still moving through the pipeline
PHASE_ASSEMBLING = "assembling"  # all slots packaged; production is assembling
PHASE_SIGNOFF = "signoff"  # assembled edition is with the director for sign-off
PHASE_PUBLISHED = "published"  # released to production to deliver/publish
PHASE_CANCELLED = "cancelled"  # cancelled by the director (ADR-0066 §6 Option B)

# Phases in which an edition is finished and no longer running.
TERMINAL_PHASES = frozenset({PHASE_PUBLISHED, PHASE_CANCELLED})


@dataclass
class Slot:
    """One story slot and where it currently sits in the per-slot pipeline."""

    slot_id: str
    title: str
    stage: str  # current pipeline stage (pipeline.SLOT_STAGES) or "done"
    artifact: str | None = None
    done: bool = False
    # The director's per-story brief (angle, length, tone, sourcing,
    # non-negotiables). Carried into every stage's delegation so the editor's
    # commission reaches the writers (#3088). Empty when only a title was given.
    brief: str = ""


@dataclass
class Edition:
    """The running picture of one edition — the engine's durable ledger."""

    edition_id: str
    theme: str
    report_to: str  # address to bring the assembled edition to (the director)
    # The message that started the edition. Retained for provenance; the engine
    # reaches the director via send_message([report_to]) (ADR-0066 §6 Option B),
    # not respond_to, since Claude — not the engine — receives the kickoff.
    origin_message_id: str = ""
    phase: str = PHASE_DRAFTING
    slots: dict[str, Slot] = field(default_factory=dict)
    assembled: str | None = None

    def all_slots_done(self) -> bool:
        return bool(self.slots) and all(s.done for s in self.slots.values())


class OrchestratorStore:
    """JSON-backed durable store rooted under the workspace volume."""

    def __init__(self, root: str | os.PathLike[str]) -> None:
        self._root = Path(root) / "orchestrator"
        self._editions_dir = self._root / "editions"
        self._correlations_path = self._root / "correlations.json"
        self._editions_dir.mkdir(parents=True, exist_ok=True)

    # --- editions --------------------------------------------------------

    def _edition_path(self, edition_id: str) -> Path:
        safe = edition_id.replace("/", "_").replace(os.sep, "_")
        return self._editions_dir / f"{safe}.json"

    def create_edition(
        self,
        *,
        edition_id: str,
        theme: str,
        slot_titles: list[str],
        report_to: str,
        first_stage: str,
        slot_briefs: list[str] | None = None,
        origin_message_id: str = "",
    ) -> Edition:
        briefs = slot_briefs or []
        slots = {
            f"slot-{i + 1}": Slot(
                slot_id=f"slot-{i + 1}",
                title=title,
                stage=first_stage,
                brief=briefs[i] if i < len(briefs) else "",
            )
            for i, title in enumerate(slot_titles)
        }
        edition = Edition(
            edition_id=edition_id,
            theme=theme,
            report_to=report_to,
            origin_message_id=origin_message_id,
            slots=slots,
        )
        self.save_edition(edition)
        return edition

    def get_edition(self, edition_id: str) -> Edition | None:
        path = self._edition_path(edition_id)
        if not path.exists():
            return None
        data = json.loads(path.read_text(encoding="utf-8"))
        slots = {sid: Slot(**sdata) for sid, sdata in data.pop("slots", {}).items()}
        return Edition(slots=slots, **data)

    def list_editions(self) -> list[Edition]:
        """Every edition on disk, newest-mtime first. Used by the engine's
        ``active_editions`` tool so Claude can discover what is running."""
        out: list[tuple[float, Edition]] = []
        for path in self._editions_dir.glob("*.json"):
            data = json.loads(path.read_text(encoding="utf-8"))
            slots = {sid: Slot(**sdata) for sid, sdata in data.pop("slots", {}).items()}
            out.append((path.stat().st_mtime, Edition(slots=slots, **data)))
        out.sort(key=lambda pair: pair[0], reverse=True)
        return [edition for _, edition in out]

    def save_edition(self, edition: Edition) -> None:
        data = asdict(edition)
        _atomic_write_json(self._edition_path(edition.edition_id), data)

    # --- correlation map -------------------------------------------------

    def _load_correlations(self) -> dict[str, dict[str, str]]:
        if not self._correlations_path.exists():
            return {}
        return json.loads(self._correlations_path.read_text(encoding="utf-8"))

    def put_correlation(
        self, ref: str, *, edition_id: str, slot_id: str, stage: str
    ) -> None:
        correlations = self._load_correlations()
        correlations[_canonical_ref(ref)] = {
            "edition_id": edition_id,
            "slot_id": slot_id,
            "stage": stage,
        }
        _atomic_write_json(self._correlations_path, correlations)

    def pop_correlation(self, ref: str) -> dict[str, str] | None:
        """Resolve and remove a correlation ref. ``None`` when unknown. Keys are
        canonicalized (#3088) so a dashed/no-dash Guid-format difference between
        the stored send-ack id and the inbound ``in_reply_to`` still matches."""
        correlations = self._load_correlations()
        entry = correlations.pop(_canonical_ref(ref), None)
        if entry is not None:
            _atomic_write_json(self._correlations_path, correlations)
        return entry


def _atomic_write_json(path: Path, data: object) -> None:
    """Write JSON atomically (temp file + replace) so a crash mid-write cannot
    corrupt durable state."""
    path.parent.mkdir(parents=True, exist_ok=True)
    fd, tmp = tempfile.mkstemp(dir=str(path.parent), suffix=".tmp")
    try:
        with os.fdopen(fd, "w", encoding="utf-8") as handle:
            json.dump(data, handle, indent=2, sort_keys=True)
        os.replace(tmp, path)
    finally:
        if os.path.exists(tmp):
            os.unlink(tmp)
