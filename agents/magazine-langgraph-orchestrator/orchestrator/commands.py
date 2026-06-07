"""
The orchestration command registry (ADR-0066 §6, #3078).

The engine co-hosts a localhost MCP server (``tools.py``) whose tool surface is
no longer a hand-maintained block of ``@server.tool()`` wrappers. Instead it is
built from a **command registry** assembled here from two *opt-in* sources:

1. **Lifecycle commands** — the edition-level operations that are not per-slot
   graph nodes (``start_edition`` / ``get_status`` / ``active_editions`` /
   ``cancel_edition`` / ``approve_edition`` / ``revise_edition``), declared as
   :class:`EngineCommand` descriptors bound to :class:`~orchestrator.coordinator.Coordinator`
   methods. This is a lossless refactor of the six tools that used to be written
   out by hand — same typed handlers, same docstrings, same schemas.

2. **Graph-derived commands** — LangGraph nodes that the workflow author has
   *explicitly annotated* as control-plane callable (via :func:`control_plane`
   in ``graph.py``, which stamps the node's LangGraph ``metadata=``). A
   reflection pass over the compiled graph collects **only** annotated nodes and
   emits an :class:`EngineCommand` for each. Un-annotated nodes emit nothing.

The default magazine pipeline annotates **no** stage node, so the graph-derived
source ships **empty** and the data plane stays closed by default — preserving
ADR-0066's "engine orchestrates, LLM manages" invariant. Exposing a stage node
to the LLM is a deliberate, per-node opt-in, never a reflection heuristic.

Discovery stays ordinary MCP ``tools/list``: ``tools.py`` registers every
descriptor and Claude sees the full current set with schemas, no extra RPC. The
``kind`` / ``stage_scope`` fields are carried on each descriptor as the seam for
a future two-step discovery surface (Alternative B in the #3078 design); they
are not consulted by the registration path today.

This module imports nothing from LangGraph, the SDK, or A2A — it reflects over
whatever compiled-graph object it is handed by duck-typing its ``builder.nodes``
metadata — so the registry is unit-testable with a tiny fake graph.
"""

from __future__ import annotations

import inspect
import logging
from dataclasses import dataclass
from typing import Any, Callable, Literal

logger = logging.getLogger("magazine-orchestrator.commands")

#: Marker key the :func:`control_plane` decorator writes into a LangGraph node's
#: ``metadata=`` dict, and which :func:`graph_derived_commands` reflects over.
#: A node carries a control-plane command descriptor iff its metadata holds this
#: key; everything else about a node is ignored by the reflection pass.
CONTROL_PLANE_METADATA_KEY = "control_plane"

CommandKind = Literal["command", "query"]


@dataclass(frozen=True)
class EngineCommand:
    """One control-plane command exposed to the LLM as an MCP tool.

    The :mod:`orchestrator.tools` server registers each command with FastMCP,
    deriving the JSON schema from ``handler``'s type hints and using ``summary``
    as the tool description (the same FastMCP mechanism the hand-written tools
    used). The handler is a plain callable — a bound coordinator method for a
    lifecycle command, or a node-derived callable for a graph-derived one — so
    FastMCP can introspect its signature.
    """

    name: str
    """The MCP tool name (unique across the registry)."""

    summary: str
    """One-line description, surfaced as the tool's MCP description."""

    handler: Callable[..., Any]
    """The callable the tool invokes. Must have an introspectable signature
    (type hints) so FastMCP can derive the input schema."""

    kind: CommandKind = "command"
    """``"command"`` (mutates orchestration state) or ``"query"`` (read-only).
    Carried as the seam for a future categorised discovery surface (#3078
    Alternative B); not consulted by registration today."""

    stage_scope: str | None = None
    """For a graph-derived command, the pipeline stage (graph node) it came
    from; ``None`` for an edition-level lifecycle command. Part of the same
    deferred-discovery seam as ``kind``."""


def control_plane(
    *,
    name: str,
    summary: str,
    kind: CommandKind = "command",
):
    """Annotate a LangGraph node function as control-plane callable (#3078).

    Applied where a node is added to the graph (``graph.py``'s ``_stage_node`` /
    ``build_slot_graph``), this stamps a marker onto the node so the registry's
    reflection pass (:func:`graph_derived_commands`) emits an
    :class:`EngineCommand` for it. A node **without** this annotation is never
    exposed — the data plane is closed by default (ADR-0066 §6).

    The marker is attached to the function object (as
    ``fn.__control_plane__``) AND returned for the caller to pass as the node's
    LangGraph ``metadata=`` (under :data:`CONTROL_PLANE_METADATA_KEY`), so the
    annotation survives both function-level introspection and a reflection pass
    over the compiled graph's node metadata. Either is sufficient; ``graph.py``
    wires the metadata path because that is what the compiled graph retains.

    The decorated function is returned unchanged (it remains a usable node);
    only the marker attribute is added.
    """

    descriptor = {"name": name, "summary": summary, "kind": kind}

    def decorate(fn: Callable[..., Any]) -> Callable[..., Any]:
        setattr(fn, "__control_plane__", descriptor)
        return fn

    # Expose the descriptor on the decorator object too, so a caller that wants
    # the metadata dict without decorating a function (the metadata= path in
    # build_slot_graph) can read it directly.
    decorate.descriptor = descriptor  # type: ignore[attr-defined]
    return decorate


def control_plane_metadata(
    *,
    name: str,
    summary: str,
    kind: CommandKind = "command",
) -> dict[str, Any]:
    """Build the LangGraph ``metadata=`` dict that marks a node control-plane
    callable (#3078) — ``{CONTROL_PLANE_METADATA_KEY: {name, summary, kind}}``.

    This is the ``metadata=`` form of :func:`control_plane`, for use at the
    ``builder.add_node(..., metadata=…)`` call site where there is no function
    to decorate in place. :func:`graph_derived_commands` reflects over exactly
    this shape.
    """
    return {
        CONTROL_PLANE_METADATA_KEY: {"name": name, "summary": summary, "kind": kind}
    }


# --- lifecycle commands ----------------------------------------------------


def lifecycle_commands(coordinator: Any) -> list[EngineCommand]:
    """The edition-level lifecycle commands, bound to *coordinator*.

    These six are the always-present control-plane surface — a lossless
    refactor of the tools that ``tools.py`` used to write out by hand. Each
    binds a typed coordinator method; ``tools.py`` derives the MCP schema from
    that method's signature and the summary below.

    ``start_edition``'s summary reproduces the #3088-tuned ``briefs`` guidance
    verbatim so the writers keep receiving the director's full per-story
    direction; the bound handler is the same typed
    :meth:`Coordinator.start_edition`, so its schema is unchanged byte-for-byte.
    """
    return [
        EngineCommand(
            name="start_edition",
            handler=coordinator.start_edition,
            kind="command",
            summary=(
                "Start a new magazine edition and return its edition_id.\n\n"
                "theme: the edition's overarching subject.\n"
                "slots: the ordered list of story-slot titles to commission "
                "(1-6).\n"
                "briefs: the director's COMPLETE direction for each story, "
                "aligned 1:1 with slots — angle, target length, tone, sourcing "
                "rules, and any non-negotiables, verbatim from the director's "
                "brief. ALWAYS pass this whenever the director specified "
                "anything beyond a bare title: the writers see ONLY what you "
                'put here, so a "150-word, no-research vignette" left out of '
                'briefs comes back as a long researched piece. Use "" for a '
                "slot the director left open.\n"
                "report_to: the director's address from the inbound message — "
                "where the assembled edition is brought for sign-off.\n\n"
                "Returns the edition_id. Use it for later get_status / "
                "cancel_edition / approve_edition / revise_edition calls "
                "(active_editions also lists it)."
            ),
        ),
        EngineCommand(
            name="get_status",
            handler=coordinator.get_status,
            kind="query",
            summary=(
                "The live status of one edition: its phase and each slot's stage."
            ),
        ),
        EngineCommand(
            name="active_editions",
            handler=coordinator.active_editions,
            kind="query",
            summary=(
                "The editions still running. Empty when nothing is in motion — "
                "call this to answer a progress question before any edition has "
                "started."
            ),
        ),
        EngineCommand(
            name="cancel_edition",
            handler=coordinator.cancel_edition,
            kind="command",
            summary="Cancel a running edition.",
        ),
        EngineCommand(
            name="approve_edition",
            handler=coordinator.approve_edition,
            kind="command",
            summary=(
                "Record the director's approval of the assembled edition and "
                "release it to production to publish. Valid only once an edition "
                "is awaiting sign-off."
            ),
        ),
        EngineCommand(
            name="revise_edition",
            handler=coordinator.revise_edition,
            kind="command",
            summary=(
                "Send the director's revision notes back to production for a "
                "revise pass. Valid only once an edition is awaiting sign-off."
            ),
        ),
    ]


# --- graph-derived commands ------------------------------------------------


def _node_specs(graph: Any) -> dict[str, Any]:
    """The compiled graph's authoring node specs, keyed by node name.

    Reflects over ``graph.builder.nodes`` — the StateGraph node specs the
    compiled graph retains — which carry the ``metadata=`` passed at
    ``add_node`` time. Returns an empty mapping for any graph object that does
    not expose that shape, so a non-LangGraph fake (or a future engine) simply
    contributes no graph-derived commands rather than erroring.
    """
    builder = getattr(graph, "builder", None)
    nodes = getattr(builder, "nodes", None)
    if isinstance(nodes, dict):
        return nodes
    return {}


def _control_plane_descriptor(node_spec: Any) -> dict[str, Any] | None:
    """Extract the control-plane descriptor a node was annotated with, or None.

    Looks first at the node's LangGraph ``metadata`` (the
    :data:`CONTROL_PLANE_METADATA_KEY` entry written by
    :func:`control_plane_metadata`), then falls back to a ``__control_plane__``
    marker on the node's callable (the :func:`control_plane` decorator form).
    A node with neither is un-annotated and yields ``None``.
    """
    metadata = getattr(node_spec, "metadata", None)
    if isinstance(metadata, dict):
        descriptor = metadata.get(CONTROL_PLANE_METADATA_KEY)
        if isinstance(descriptor, dict):
            return descriptor

    runnable = getattr(node_spec, "runnable", None)
    for candidate in (runnable, getattr(runnable, "func", None)):
        descriptor = getattr(candidate, "__control_plane__", None)
        if isinstance(descriptor, dict):
            return descriptor
    return None


def graph_derived_commands(
    graph: Any, *, handler_factory: Callable[[str, dict[str, Any]], Callable[..., Any]]
) -> list[EngineCommand]:
    """Reflect over *graph* and emit a command for each **annotated** node.

    Only nodes carrying a control-plane annotation (see
    :func:`_control_plane_descriptor`) contribute; un-annotated nodes emit
    nothing. ``handler_factory(node_name, descriptor)`` builds the callable the
    resulting tool invokes — the graph module owns how an annotated node maps to
    a coordinator-routed handler, so the registry never touches graph state
    (ADR-0066 §6: the LLM passes identifiers, the coordinator owns resume).

    The default magazine pipeline annotates no node, so this returns ``[]`` and
    the seam ships empty. Each emitted command's ``stage_scope`` is the node
    name it derived from.
    """
    commands: list[EngineCommand] = []
    for node_name, node_spec in _node_specs(graph).items():
        descriptor = _control_plane_descriptor(node_spec)
        if descriptor is None:
            continue
        name = str(descriptor.get("name") or node_name)
        commands.append(
            EngineCommand(
                name=name,
                summary=str(descriptor.get("summary") or ""),
                handler=handler_factory(node_name, descriptor),
                kind=descriptor.get("kind", "command"),
                stage_scope=node_name,
            )
        )
    return commands


# --- registry assembly -----------------------------------------------------


def build_command_registry(
    coordinator: Any,
    graph: Any = None,
    *,
    handler_factory: Callable[[str, dict[str, Any]], Callable[..., Any]] | None = None,
) -> list[EngineCommand]:
    """Merge the lifecycle commands with the graph-derived (annotated-node)
    commands into the registry the tool server publishes (#3078).

    Validates that every command name is unique and that every handler has an
    introspectable signature (FastMCP needs the type hints to build the schema);
    a duplicate name or a non-introspectable handler is a wiring bug and raises.

    *graph* is the compiled LangGraph the lifecycle coordinator also drives;
    ``None`` (or any object without the ``builder.nodes`` shape) simply
    contributes no graph-derived commands, so the registry is the lifecycle six.

    *handler_factory* builds the callable for a graph-derived command from its
    node name + descriptor; it defaults to a guard that refuses to register a
    graph-derived command without one, since the default pipeline annotates no
    node and ships the seam empty. The day a node is annotated, the caller must
    supply a factory that routes the node through the coordinator.
    """

    def _reject_unwired(node_name: str, _descriptor: dict[str, Any]):
        raise ValueError(
            f"graph node '{node_name}' is annotated control-plane but no "
            "handler_factory was supplied to route it through the coordinator"
        )

    lifecycle = list(lifecycle_commands(coordinator))
    derived = graph_derived_commands(
        graph, handler_factory=handler_factory or _reject_unwired
    )
    commands = [*lifecycle, *derived]

    seen: set[str] = set()
    for command in commands:
        if command.name in seen:
            raise ValueError(f"duplicate command name in registry: {command.name!r}")
        seen.add(command.name)
        if not callable(command.handler):
            raise ValueError(f"command {command.name!r} has a non-callable handler")
        try:
            inspect.signature(command.handler)
        except (TypeError, ValueError) as exc:  # pragma: no cover - defensive
            raise ValueError(
                f"command {command.name!r} handler is not introspectable: {exc}"
            ) from exc

    logger.info(
        "Command registry built: %d command(s) (%d lifecycle, %d graph-derived)",
        len(commands),
        len(lifecycle),
        len(derived),
    )
    return commands
