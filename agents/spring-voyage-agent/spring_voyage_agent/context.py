"""
IAgentContext — bootstrap bundle delivered to the SDK at initialize().

Implements docs/specs/agent-runtime-boundary.md §2:
  - Reads all required env vars (§2.2.1).
  - Reads structured files from /spring/context/ (§2.2.2).
  - Raises a fatal error if any required field is missing.
"""

from __future__ import annotations

import json
import logging
import os
from dataclasses import dataclass, field
from pathlib import Path
from typing import Any

logger = logging.getLogger("spring-voyage-agent.context")

# Canonical env var names per spec §2.2.1.
_ENV_TENANT_ID = "SPRING_TENANT_ID"
_ENV_UNIT_ID = "SPRING_UNIT_ID"
_ENV_AGENT_ID = "SPRING_AGENT_ID"
_ENV_BUCKET2_URL = "SPRING_BUCKET2_URL"
_ENV_BUCKET2_TOKEN = "SPRING_BUCKET2_TOKEN"
_ENV_LLM_PROVIDER_URL = "SPRING_LLM_PROVIDER_URL"
_ENV_LLM_PROVIDER_TOKEN = "SPRING_LLM_PROVIDER_TOKEN"
_ENV_MCP_URL = "SPRING_MCP_URL"
_ENV_MCP_TOKEN = "SPRING_MCP_TOKEN"
_ENV_TELEMETRY_URL = "SPRING_TELEMETRY_URL"
_ENV_TELEMETRY_TOKEN = "SPRING_TELEMETRY_TOKEN"
_ENV_WORKSPACE_PATH = "SPRING_WORKSPACE_PATH"
_ENV_CONCURRENT_THREADS = "SPRING_CONCURRENT_THREADS"

# Canonical mount path per spec §2.2.2.
_CONTEXT_DIR = Path("/spring/context")
_AGENT_DEF_YAML = _CONTEXT_DIR / "agent-definition.yaml"
_AGENT_DEF_JSON = _CONTEXT_DIR / "agent-definition.json"
_TENANT_CONFIG_JSON = _CONTEXT_DIR / "tenant-config.json"

_REQUIRED_ENV_VARS = (
    _ENV_TENANT_ID,
    _ENV_AGENT_ID,
    _ENV_BUCKET2_URL,
    _ENV_BUCKET2_TOKEN,
    _ENV_LLM_PROVIDER_URL,
    _ENV_LLM_PROVIDER_TOKEN,
    _ENV_MCP_URL,
    _ENV_MCP_TOKEN,
    _ENV_TELEMETRY_URL,
    _ENV_WORKSPACE_PATH,
    _ENV_CONCURRENT_THREADS,
)


class ContextLoadError(RuntimeError):
    """Raised when a required IAgentContext field is missing or unreadable.

    The SDK surfaces this as a fatal initialize() failure per spec §2.3.
    """


@dataclass
class IAgentContext:
    """Read-only bootstrap bundle for the agent container.

    Spec: docs/specs/agent-runtime-boundary.md §2.1.

    Construct via :meth:`load` rather than directly; :meth:`load` reads env
    vars and mounted files, validates required fields, and raises
    :class:`ContextLoadError` on missing required fields.
    """

    # Static metadata
    tenant_id: str
    agent_id: str
    unit_id: str | None

    # Bucket-2 endpoint
    bucket2_url: str
    bucket2_token: str

    # Platform-provided service endpoints
    llm_provider_url: str
    llm_provider_token: str
    mcp_url: str
    mcp_token: str
    telemetry_url: str
    telemetry_token: str | None

    # Workspace mount path
    workspace_path: str

    # Concurrent-threads policy
    concurrent_threads: bool

    # Structured documents (from mounted files)
    agent_definition: dict[str, Any] = field(default_factory=dict)
    tenant_config: dict[str, Any] = field(default_factory=dict)

    @classmethod
    def load(cls) -> "IAgentContext":
        """Read IAgentContext from environment variables and mounted files.

        Validates that all required env vars are present and non-empty.
        Raises :class:`ContextLoadError` for any missing required field.

        Spec: docs/specs/agent-runtime-boundary.md §2.2.
        """
        missing = [v for v in _REQUIRED_ENV_VARS if not os.environ.get(v)]
        if missing:
            raise ContextLoadError(
                f"Missing required IAgentContext env vars: {', '.join(missing)}. "
                "The platform must populate these before container start (spec §2.2.1)."
            )

        concurrent_threads_raw = os.environ[_ENV_CONCURRENT_THREADS].lower()
        if concurrent_threads_raw not in ("true", "false"):
            raise ContextLoadError(
                f"SPRING_CONCURRENT_THREADS must be 'true' or 'false', got: {os.environ[_ENV_CONCURRENT_THREADS]!r}"
            )

        agent_definition = _load_agent_definition()
        tenant_config = _load_tenant_config()

        ctx = cls(
            tenant_id=os.environ[_ENV_TENANT_ID],
            agent_id=os.environ[_ENV_AGENT_ID],
            unit_id=os.environ.get(_ENV_UNIT_ID) or None,
            bucket2_url=os.environ[_ENV_BUCKET2_URL],
            bucket2_token=os.environ[_ENV_BUCKET2_TOKEN],
            llm_provider_url=os.environ[_ENV_LLM_PROVIDER_URL],
            llm_provider_token=os.environ[_ENV_LLM_PROVIDER_TOKEN],
            mcp_url=os.environ[_ENV_MCP_URL],
            mcp_token=os.environ[_ENV_MCP_TOKEN],
            telemetry_url=os.environ[_ENV_TELEMETRY_URL],
            telemetry_token=os.environ.get(_ENV_TELEMETRY_TOKEN) or None,
            workspace_path=os.environ[_ENV_WORKSPACE_PATH],
            concurrent_threads=(concurrent_threads_raw == "true"),
            agent_definition=agent_definition,
            tenant_config=tenant_config,
        )

        logger.info(
            "IAgentContext loaded: tenant=%s agent=%s unit=%s concurrent_threads=%s workspace=%s",
            ctx.tenant_id,
            ctx.agent_id,
            ctx.unit_id,
            ctx.concurrent_threads,
            ctx.workspace_path,
        )
        return ctx


def _load_agent_definition() -> dict[str, Any]:
    """Read /spring/context/agent-definition.yaml or .json.

    Spec §2.2.2: the SDK MUST check both extensions.
    The YAML extension is tried first; JSON is tried second.
    Returns an empty dict (and logs a warning) if neither file exists, so
    the SDK does not hard-fail when running in a local dev harness.
    """
    # Try YAML first (preferred per spec prose).
    if _AGENT_DEF_YAML.exists():
        try:
            import yaml  # type: ignore[import-untyped]

            with _AGENT_DEF_YAML.open() as f:
                data = yaml.safe_load(f)
            return data if isinstance(data, dict) else {}
        except Exception as exc:
            logger.warning("Failed to parse %s: %s", _AGENT_DEF_YAML, exc)
            return {}

    # Fall back to JSON.
    if _AGENT_DEF_JSON.exists():
        try:
            with _AGENT_DEF_JSON.open() as f:
                data = json.load(f)
            return data if isinstance(data, dict) else {}
        except Exception as exc:
            logger.warning("Failed to parse %s: %s", _AGENT_DEF_JSON, exc)
            return {}

    # Neither file present — warn but do not fail.  A local harness may not
    # mount the context directory.
    logger.warning(
        "agent-definition not found at %s or %s — running without it",
        _AGENT_DEF_YAML,
        _AGENT_DEF_JSON,
    )
    return {}


def _load_tenant_config() -> dict[str, Any]:
    """Read /spring/context/tenant-config.json if present.

    The file is optional per spec §2.2.2; returns an empty dict when absent.
    """
    if _TENANT_CONFIG_JSON.exists():
        try:
            with _TENANT_CONFIG_JSON.open() as f:
                data = json.load(f)
            return data if isinstance(data, dict) else {}
        except Exception as exc:
            logger.warning("Failed to parse %s: %s", _TENANT_CONFIG_JSON, exc)
    return {}
