"""
A2A server setup for the Dapr Agent.

Exposes an Agent Card, wires up the request handler and task store, and
provides a factory that returns a Starlette ASGI application ready to be
served by Uvicorn.

Migration note (a2a-sdk 1.x): the old A2AStarletteApplication wrapper was
removed in 1.0.  The equivalent is a plain Starlette application composed
from create_rest_routes() + create_agent_card_routes().  AgentCard, AgentSkill,
and AgentCapabilities are now protobuf types; the url field moved from the
top-level card onto supported_interfaces[0].url (AgentInterface).  See issue
#940.
"""

from __future__ import annotations

import logging
import os
from typing import Any

from a2a.server.events.in_memory_queue_manager import InMemoryQueueManager
from a2a.server.request_handlers import DefaultRequestHandler
from a2a.server.routes import create_agent_card_routes, create_rest_routes
from a2a.server.tasks import InMemoryTaskStore
from a2a.types import (
    AgentCapabilities,
    AgentCard,
    AgentSkill,
)
from a2a.types.a2a_pb2 import AgentInterface
from starlette.applications import Starlette

logger = logging.getLogger("dapr-agent.a2a")

DEFAULT_PORT = 8999


def build_agent_card(
    *,
    port: int | None = None,
    model: str | None = None,
    provider: str | None = None,
) -> AgentCard:
    """Build an A2A Agent Card describing this Dapr Agent."""
    port = port or int(os.environ.get("AGENT_PORT", str(DEFAULT_PORT)))
    model = model or os.environ.get("SPRING_MODEL", "llama3.2:3b")
    provider = provider or os.environ.get("SPRING_LLM_PROVIDER", "ollama")

    skill = AgentSkill(
        id="dapr-agent-execute",
        name="Execute Task",
        description=(
            f"Runs an agentic loop against {provider}/{model} with MCP tool access. "
            "Suitable for general-purpose tasks including coding, analysis, and research."
        ),
        tags=["dapr", provider, model],
        examples=["Summarize the open issues", "Fix the failing test"],
    )

    return AgentCard(
        name=f"Spring Voyage Dapr Agent ({provider}/{model})",
        description=(
            "Platform-managed agentic loop powered by Dapr Agents. "
            f"Uses {provider}/{model} for inference and MCP for tool access."
        ),
        # In a2a-sdk 1.x the agent URL lives in supported_interfaces rather
        # than as a top-level field on AgentCard.
        supported_interfaces=[AgentInterface(url=f"http://localhost:{port}")],
        version="1.0.0",
        default_input_modes=["text"],
        default_output_modes=["text"],
        capabilities=AgentCapabilities(streaming=True),
        skills=[skill],
    )


def create_a2a_app(
    agent_executor: Any,
    *,
    port: int | None = None,
) -> Starlette:
    """Create the A2A Starlette application.

    In a2a-sdk 1.x the A2AStarletteApplication wrapper is gone.  The equivalent
    is a plain Starlette application whose routes are produced by
    create_rest_routes() (the A2A protocol endpoints) and
    create_agent_card_routes() (the well-known agent-card endpoint).
    DefaultRequestHandler now requires agent_card and queue_manager in addition
    to agent_executor and task_store.
    """
    card = build_agent_card(port=port)

    task_store = InMemoryTaskStore()
    queue_manager = InMemoryQueueManager()
    handler = DefaultRequestHandler(
        agent_executor=agent_executor,
        task_store=task_store,
        agent_card=card,
        queue_manager=queue_manager,
    )

    # Order matters. create_rest_routes registers a /{tenant}/{path:.*} mount
    # that matches every two-segment path — including /.well-known/agent-card.json
    # and /.well-known/agent.json — so the agent-card routes MUST be registered
    # first. The second create_agent_card_routes call adds the legacy
    # /.well-known/agent.json alias that the smoke contract
    # (smoke-agent-images.sh) and existing consumers expect alongside the SDK 1.x
    # canonical /.well-known/agent-card.json path.
    routes = (
        create_agent_card_routes(card)
        + create_agent_card_routes(card, card_url="/.well-known/agent.json")
        + create_rest_routes(handler)
    )
    app = Starlette(routes=routes)

    logger.info("A2A server configured: %s", card.name)
    return app
