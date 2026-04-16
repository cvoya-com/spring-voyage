"""
A2A server setup for the Dapr Agent.

Exposes an Agent Card, wires up the request handler and task store, and
provides a factory that returns a Starlette ASGI application ready to be
served by Uvicorn.
"""

from __future__ import annotations

import logging
import os
from typing import Any

from a2a.server.apps import A2AStarletteApplication
from a2a.server.request_handlers import DefaultRequestHandler
from a2a.server.tasks import InMemoryTaskStore
from a2a.types import (
    AgentCapabilities,
    AgentCard,
    AgentSkill,
)

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
        url=f"http://localhost:{port}",
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
) -> A2AStarletteApplication:
    """Create the A2A Starlette application."""
    card = build_agent_card(port=port)

    handler = DefaultRequestHandler(
        agent_executor=agent_executor,
        task_store=InMemoryTaskStore(),
    )

    app = A2AStarletteApplication(
        agent_card=card,
        http_handler=handler,
    )

    logger.info("A2A server configured: %s", card.name)
    return app
