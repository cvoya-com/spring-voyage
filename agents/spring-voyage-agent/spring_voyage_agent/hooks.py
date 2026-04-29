"""
AgentHooks — typed container for the three lifecycle callbacks.

Agent authors supply callables that match the hook signatures below; the
SDK runtime calls them in the correct order. AgentHooks is a thin wrapper
so the runtime can introspect and validate the hooks before starting.
"""

from __future__ import annotations

from dataclasses import dataclass
from typing import Any, AsyncIterable, Callable, Coroutine, Iterable

# Callable type aliases for the three hooks.
# We keep them as Protocol-style annotations rather than concrete types
# so authors can use plain functions, methods, or callable objects interchangeably.
InitializeHook = Callable[..., Coroutine[Any, Any, None]]
OnMessageHook = Callable[..., AsyncIterable[Any] | Iterable[Any] | Coroutine[Any, Any, Any]]
OnShutdownHook = Callable[..., Coroutine[Any, Any, None]]


@dataclass
class AgentHooks:
    """Typed container for the three Bucket 1 lifecycle hooks.

    Spec: docs/specs/agent-runtime-boundary.md §1.

    Pass an instance to :func:`spring_voyage_agent.run` or construct the SDK
    runtime directly with :class:`spring_voyage_agent.runtime.AgentRuntime`.
    """

    initialize: InitializeHook
    """Invoked exactly once at container start with the IAgentContext bundle."""

    on_message: OnMessageHook
    """Invoked per inbound A2A message; returns a stream of Response chunks."""

    on_shutdown: OnShutdownHook
    """Invoked exactly once when the platform terminates the container (SIGTERM)."""
