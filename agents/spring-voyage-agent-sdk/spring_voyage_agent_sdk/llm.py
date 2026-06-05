"""
One-shot Claude completion for deterministic ``a2a-process`` engines (ADR-0066).

A deterministic orchestration engine owns routing and graph state, but the
*natural-language* steps — interpreting a free-form brief, composing a
human-facing reply — are an LLM's job. Rather than teach the engine how to
launch an LLM, the SDK abstracts the co-hosted ``claude`` CLI behind a single
:func:`complete` call. The engine stays LLM-agnostic: it hands over a prompt and
gets text back.

The invocation mirrors the platform's claude-code runtime
(``ClaudeCodeLauncher.BaseClaudeArgv``): ``claude --print
--dangerously-skip-permissions --output-format json``, authenticated by the
OAuth token the ``a2a-process`` launcher injects as ``CLAUDE_CODE_OAUTH_TOKEN``.
No MCP server is wired in — this is a pure text-in/text-out turn; the engine
owns all platform I/O. The assistant's text is the ``result`` field of the JSON
envelope the CLI emits (the same field the agent-sidecar bridge reads).
"""

from __future__ import annotations

import asyncio
import contextlib
import json
import logging
import os
import tempfile
from pathlib import Path

logger = logging.getLogger("spring-voyage-agent-sdk.llm")

# Mirrors ClaudeCodeLauncher.BaseClaudeArgv. `--print` is non-interactive;
# `--output-format json` makes the CLI emit a single JSON envelope whose
# `result` carries the assistant text.
_CLAUDE_BASE_ARGV = (
    "claude",
    "--print",
    "--dangerously-skip-permissions",
    "--output-format",
    "json",
)

# The CLI flag that appends a file's contents to the system prompt (Append mode;
# ClaudeCodeLauncher.AppendSystemPromptFileFlag).
_APPEND_SYSTEM_PROMPT_FILE_FLAG = "--append-system-prompt-file"

_DEFAULT_TIMEOUT_SECONDS = 120.0


class ClaudeCompletionError(RuntimeError):
    """Raised when the ``claude`` CLI is unavailable, times out, or errors.

    Callers should treat this as recoverable — a deterministic engine falls
    back to a heuristic rather than failing the turn.
    """


async def complete(
    prompt: str,
    *,
    system_prompt: str | None = None,
    system_prompt_file: str | None = None,
    mcp_config_path: str | None = None,
    timeout: float = _DEFAULT_TIMEOUT_SECONDS,
) -> str:
    """Run one Claude turn over *prompt* and return the assistant's text.

    Parameters
    ----------
    prompt:
        The user message, delivered on the CLI's stdin.
    system_prompt:
        Inline system-prompt text. When given it is written to a temp file and
        appended via ``--append-system-prompt-file`` (and *system_prompt_file*
        is ignored). Use this for a focused instruction (e.g. "extract a theme
        and story slots as JSON") rather than the agent's full persona.
    system_prompt_file:
        Path to a system-prompt file to append. Used only when *system_prompt*
        is ``None`` (e.g. the platform-assembled ``.spring/system-prompt.md``).
        Skipped if the path does not exist.
    mcp_config_path:
        Path to a Claude ``.mcp.json`` (``--mcp-config``). When given, Claude
        runs *with tools* — it can call the MCP servers named in that config
        (e.g. the orchestrator's local tool server, ADR-0066 §6 Option B).
        ``--dangerously-skip-permissions`` (already in the base argv) lets it
        call them without prompting.
    timeout:
        Seconds before the turn is abandoned (the process is killed).

    Raises
    ------
    ClaudeCompletionError
        If the CLI is missing, exits non-zero, or exceeds *timeout*.
    """
    argv = list(_CLAUDE_BASE_ARGV)
    if mcp_config_path:
        argv += ["--mcp-config", str(mcp_config_path)]
    tmp_path: str | None = None
    try:
        effective_sp_file = system_prompt_file
        if system_prompt is not None:
            fd, tmp_path = tempfile.mkstemp(suffix=".md", prefix="sv-complete-")
            with os.fdopen(fd, "w", encoding="utf-8") as handle:
                handle.write(system_prompt)
            effective_sp_file = tmp_path
        if effective_sp_file and Path(effective_sp_file).exists():
            argv += [_APPEND_SYSTEM_PROMPT_FILE_FLAG, str(effective_sp_file)]

        try:
            proc = await asyncio.create_subprocess_exec(
                *argv,
                stdin=asyncio.subprocess.PIPE,
                stdout=asyncio.subprocess.PIPE,
                stderr=asyncio.subprocess.PIPE,
            )
        except FileNotFoundError as exc:
            raise ClaudeCompletionError(
                "`claude` CLI not found on PATH — the a2a-process engine image "
                "must co-host the Claude Code CLI for natural-language steps."
            ) from exc

        try:
            stdout, stderr = await asyncio.wait_for(proc.communicate(prompt.encode("utf-8")), timeout)
        except asyncio.TimeoutError as exc:
            with contextlib.suppress(ProcessLookupError):
                proc.kill()
            raise ClaudeCompletionError(f"`claude` did not complete within {timeout:g}s") from exc

        if proc.returncode != 0:
            detail = stderr.decode("utf-8", "replace").strip()[:500]
            raise ClaudeCompletionError(f"`claude` exited {proc.returncode}: {detail}")

        return _extract_result_text(stdout.decode("utf-8", "replace"))
    finally:
        if tmp_path:
            with contextlib.suppress(OSError):
                os.unlink(tmp_path)


def _extract_result_text(stdout: str) -> str:
    """Pull the assistant text out of ``claude --output-format json`` stdout.

    The CLI emits a JSON object whose ``result`` is the assistant's text (the
    field the agent-sidecar bridge also reads). Best-effort: if the output is
    not the expected JSON shape, return it verbatim so a caller still gets
    *something* usable.
    """
    text = stdout.strip()
    if not text:
        return ""
    try:
        parsed = json.loads(text)
    except (ValueError, TypeError):
        return text
    if isinstance(parsed, dict):
        result = parsed.get("result")
        if isinstance(result, str):
            return result
    return text
