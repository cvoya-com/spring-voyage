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

# SPRING_MCP_TOKEN is intentionally NOT required (ADR-0066 §2). For a
# persistent/always-on runtime the dispatcher stamps it empty at cold-start and
# delivers the real per-turn token in each inbound message's A2A metadata
# (surfaced as `Message.mcp_token`); requiring it non-empty here would make an
# always-on a2a-process fail to initialise. Per-turn CLI runtimes still receive
# a non-empty value, so this is a strict relaxation.
#
# SPRING_TELEMETRY_URL is also NOT required (#2916). Telemetry is conditional /
# opt-in infrastructure (spec §2.2.1): AgentContextBuilder emits the telemetry
# vars only when a collector is configured and omits them otherwise. Requiring
# the URL here would make any deployment without telemetry fail to bootstrap an
# SDK agent. Telemetry export is best-effort when the endpoint is absent.
_REQUIRED_ENV_VARS = (
    _ENV_TENANT_ID,
    _ENV_AGENT_ID,
    _ENV_BUCKET2_URL,
    _ENV_BUCKET2_TOKEN,
    _ENV_LLM_PROVIDER_URL,
    _ENV_LLM_PROVIDER_TOKEN,
    _ENV_MCP_URL,
    _ENV_WORKSPACE_PATH,
    _ENV_CONCURRENT_THREADS,
)


class ContextLoadError(RuntimeError):
    """Raised when a required IAgentContext field is missing or unreadable.

    The SDK surfaces this as a fatal initialize() failure per spec §2.3.
    """


class IAgentContext:
    """Read-only bootstrap bundle for the agent container.

    Spec: docs/specs/agent-runtime-boundary.md §2.1.

    Construct via :meth:`load` rather than directly; :meth:`load` reads env
    vars and mounted files, validates required fields, and raises
    :class:`ContextLoadError` on missing required fields.

    :attr:`system_prompt` is a fresh-read property (#2734): each access
    re-reads ``$SPRING_WORKSPACE_PATH/.spring/system-prompt.md`` from
    disk so the SDK's per-turn bootstrap integrity check (ADR-0055 §6,
    :class:`spring_voyage_agent_sdk.bootstrap.BootstrapFetcher.integrity_check_and_refresh`)
    actually flows through to ``on_message``. Caching the bytes once at
    :meth:`load` time would defeat the integrity check — operator
    edits to the agent's instructions or connector contributions would
    not take effect until the container restarted.
    """

    def __init__(
        self,
        *,
        tenant_id: str,
        agent_id: str,
        unit_id: str | None,
        thread_id: str | None,
        bucket2_url: str,
        bucket2_token: str,
        llm_provider_url: str,
        llm_provider_token: str,
        mcp_url: str,
        mcp_token: str,
        telemetry_url: str | None,
        telemetry_token: str | None,
        workspace_path: str,
        concurrent_threads: bool,
    ) -> None:
        # Static metadata
        self.tenant_id = tenant_id
        self.agent_id = agent_id
        self.unit_id = unit_id
        # Thread id — present when the container launch was triggered by a
        # dispatch on a known thread (every normal message dispatch); None on
        # supervisor-driven restarts (agent-level lifecycle events, not bound
        # to any thread). Spec §2.2.1 (`SPRING_THREAD_ID`, optional).
        self.thread_id = thread_id

        # Bucket-2 endpoint
        self.bucket2_url = bucket2_url
        self.bucket2_token = bucket2_token

        # Platform-provided service endpoints
        self.llm_provider_url = llm_provider_url
        self.llm_provider_token = llm_provider_token
        self.mcp_url = mcp_url
        self.mcp_token = mcp_token
        self.telemetry_url = telemetry_url
        self.telemetry_token = telemetry_token

        # Workspace mount path — every file the platform contributes to the
        # bundle lives under this root (the SDK's bootstrap client writes the
        # bundle here on container start and refreshes the platform-
        # authoritative subset on every on_message).
        self.workspace_path = workspace_path

        # Concurrent-threads policy
        self.concurrent_threads = concurrent_threads

    @property
    def system_prompt(self) -> str | None:
        """Platform-assembled system prompt (spec §2.2.2).

        Reads ``$SPRING_WORKSPACE_PATH/.spring/system-prompt.md`` on
        every access so the SDK's per-turn bootstrap integrity check
        (ADR-0055 §6) is observable to ``on_message``. Returns
        ``None`` (and logs a warning) when the file is absent — e.g.
        a local dev harness that hasn't mounted the workspace, or a
        launcher that did not contribute a system-prompt file.
        """
        return _read_system_prompt(self.workspace_path)

    def thread_workspace(self, thread_id: str) -> Path:
        """Return the on-disk scratch directory for this conversation.

        Per-conversation scratch lives under
        ``$SPRING_WORKSPACE_PATH/work/<id>/``, where ``<id>`` is an opaque,
        platform-managed segment that means nothing to the agent (#3041).
        This helper returns that path and creates the directory on first
        access (``mkdir(parents=True, exist_ok=True)``). The subtree is
        ephemeral scratch; durable state belongs in ``sv.memory.*``.

        ``thread_id`` is the platform-assigned id exposed on every
        ``on_message`` invocation as :attr:`Message.thread_id` (the A2A
        SDK's ``Message.context_id``); it is used only to keep concurrent
        conversations' scratch directories distinct.

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

        path = Path(self.workspace_path) / "work" / thread_id
        path.mkdir(parents=True, exist_ok=True)
        return path

    async def complete(self, prompt: str, *, system_prompt: str | None = None) -> str:
        """Run one Claude turn over *prompt* and return the assistant's text.

        ADR-0066: a deterministic ``a2a-process`` engine owns orchestration but
        delegates natural-language steps — interpreting a free-form brief,
        composing a human-facing reply — to Claude. This abstracts the
        co-hosted ``claude`` CLI so the engine code never launches an LLM
        itself; it authenticates with the OAuth token the ``a2a-process``
        launcher injected (``CLAUDE_CODE_OAUTH_TOKEN``).

        With *system_prompt* unset the platform-assembled persona
        (``.spring/system-prompt.md``) is appended; pass *system_prompt* to
        substitute a focused instruction instead (e.g. a structured-extraction
        directive). See :func:`spring_voyage_agent_sdk.llm.complete`.
        """
        from spring_voyage_agent_sdk import llm

        return await llm.complete(
            prompt,
            system_prompt=system_prompt,
            system_prompt_file=str(Path(self.workspace_path) / _SYSTEM_PROMPT_REL_PATH),
        )

    @classmethod
    def load(cls) -> "IAgentContext":
        """Read IAgentContext from environment variables.

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
            # Optional (ADR-0066 §2): per-turn token arrives on each message for
            # always-on runtimes. Empty string when the platform deferred it.
            mcp_token=os.environ.get(_ENV_MCP_TOKEN, ""),
            # Optional (#2916): telemetry is opt-in. The builder omits
            # SPRING_TELEMETRY_URL when no collector is configured; the agent
            # then runs without telemetry export rather than failing to boot.
            telemetry_url=os.environ.get(_ENV_TELEMETRY_URL) or None,
            telemetry_token=os.environ.get(_ENV_TELEMETRY_TOKEN) or None,
            workspace_path=os.environ[_ENV_WORKSPACE_PATH],
            concurrent_threads=(concurrent_threads_raw == "true"),
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


def _read_system_prompt(workspace_path: str) -> str | None:
    """Read the platform-assembled system prompt from the workspace mount.

    Spec §2.2.2 — the platform delivers the system prompt at
    ``$SPRING_WORKSPACE_PATH/.spring/system-prompt.md``. The platform's
    bootstrap mechanism (ADR-0055) re-pulls and re-writes this file on
    every ``on_message`` so changes to the agent definition take effect
    at turn cadence; :attr:`IAgentContext.system_prompt` reads the file
    fresh on each access to surface those changes.

    Returns ``None`` (and logs a warning) when the file is absent or
    unreadable — e.g. a local dev harness that hasn't mounted the
    workspace, or a launcher that did not contribute a system-prompt
    file.
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
