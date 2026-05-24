"""
IAgentContext — bootstrap bundle delivered to the SDK at initialize().

Implements docs/specs/agent-runtime-boundary.md §2:
  - Reads all required env vars (§2.2.1).
  - Reads the platform-assembled system prompt from the workspace mount (§2.2.2).
  - Raises a fatal error if any required field is missing.
"""

from __future__ import annotations

import logging
import os
from dataclasses import dataclass
from pathlib import Path

logger = logging.getLogger("spring-voyage-agent-sdk.context")

# Canonical env var names per spec §2.2.1.
_ENV_TENANT_ID = "SPRING_TENANT_ID"
_ENV_UNIT_ID = "SPRING_UNIT_ID"
_ENV_AGENT_ID = "SPRING_AGENT_ID"
_ENV_THREAD_ID = "SPRING_THREAD_ID"
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

# Workspace-relative path of the platform-assembled system prompt (spec §2.2.2).
_SYSTEM_PROMPT_REL_PATH = Path(".spring") / "system-prompt.md"

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
    thread_id: str | None
    """The Spring Voyage thread id associated with this launch.

    Present when the container launch was triggered by a dispatch on a known
    thread (i.e. every normal message dispatch).  ``None`` on supervisor-driven
    restarts, which are agent-level lifecycle events not bound to any particular
    thread.

    Spec: docs/specs/agent-runtime-boundary.md §2.1, §2.2.1
    (``SPRING_THREAD_ID``, optional).
    """

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

    # Platform-assembled system prompt (spec §2.2.2). ``None`` when the file
    # is absent (e.g. local dev harness, or a launcher that has not yet
    # contributed a system-prompt file).
    system_prompt: str | None = None

    def thread_workspace(self, thread_id: str) -> Path:
        """Return the on-disk workspace directory for ``thread_id``.

        Per ADR-0041 (`docs/decisions/0041-actor-runtime-contract.md`),
        on-disk per-thread state lives under
        ``$SPRING_WORKSPACE_PATH/threads/<thread.id>/``.  This helper
        returns that path and creates the directory on first access
        (``mkdir(parents=True, exist_ok=True)``).

        ``thread_id`` is the platform-assigned thread id exposed on every
        ``on_message`` invocation as :attr:`Message.thread_id` (the A2A
        SDK's ``Message.context_id``).

        Parameters
        ----------
        thread_id:
            The Spring Voyage thread id.  MUST be non-empty.

        Returns
        -------
        Path
            The thread-local directory; safe to write into immediately.

        Raises
        ------
        ValueError
            ``thread_id`` is empty or whitespace.
        """
        if not thread_id or not thread_id.strip():
            raise ValueError("thread_id must be a non-empty string")

        path = Path(self.workspace_path) / "threads" / thread_id
        path.mkdir(parents=True, exist_ok=True)
        return path

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

        workspace_path = os.environ[_ENV_WORKSPACE_PATH]
        system_prompt = _load_system_prompt(workspace_path)

        ctx = cls(
            tenant_id=os.environ[_ENV_TENANT_ID],
            agent_id=os.environ[_ENV_AGENT_ID],
            unit_id=os.environ.get(_ENV_UNIT_ID) or None,
            thread_id=os.environ.get(_ENV_THREAD_ID) or None,
            bucket2_url=os.environ[_ENV_BUCKET2_URL],
            bucket2_token=os.environ[_ENV_BUCKET2_TOKEN],
            llm_provider_url=os.environ[_ENV_LLM_PROVIDER_URL],
            llm_provider_token=os.environ[_ENV_LLM_PROVIDER_TOKEN],
            mcp_url=os.environ[_ENV_MCP_URL],
            mcp_token=os.environ[_ENV_MCP_TOKEN],
            telemetry_url=os.environ[_ENV_TELEMETRY_URL],
            telemetry_token=os.environ.get(_ENV_TELEMETRY_TOKEN) or None,
            workspace_path=workspace_path,
            concurrent_threads=(concurrent_threads_raw == "true"),
            system_prompt=system_prompt,
        )

        logger.info(
            "IAgentContext loaded: tenant=%s agent=%s unit=%s thread=%s concurrent_threads=%s workspace=%s",
            ctx.tenant_id,
            ctx.agent_id,
            ctx.unit_id,
            ctx.thread_id,
            ctx.concurrent_threads,
            ctx.workspace_path,
        )
        return ctx


def _load_system_prompt(workspace_path: str) -> str | None:
    """Read the platform-assembled system prompt from the workspace mount.

    Spec §2.2.2: the platform delivers the system prompt at
    ``$SPRING_WORKSPACE_PATH/.spring/system-prompt.md``. The platform's
    bootstrap mechanism (ADR-0055) re-pulls it on every turn so changes to
    the agent definition take effect at next-turn cadence.

    Returns ``None`` (and logs a warning) when the file is absent — e.g. a
    local dev harness that hasn't mounted the workspace, or a launcher that
    has not yet contributed a system-prompt file.
    """
    path = Path(workspace_path) / _SYSTEM_PROMPT_REL_PATH
    if not path.exists():
        logger.warning("system-prompt not found at %s — running without it", path)
        return None
    try:
        return path.read_text(encoding="utf-8")
    except Exception as exc:
        logger.warning("Failed to read %s: %s", path, exc)
        return None
