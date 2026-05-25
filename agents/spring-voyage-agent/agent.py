#!/usr/bin/env python3
"""
Spring Voyage Dapr Agent — migrated onto the Spring Voyage Agent SDK.

Implements the three SDK lifecycle hooks (initialize / on_message /
on_shutdown) and hands them to the SDK runtime.

Why no Dapr Workflow / DurableAgent: ADR 0029 Stage 0 (issue #1199
follow-up). An ephemeral agent is defined by container-lifetime-tied-to-
completion-of-work, not by any specific durability mechanism. Going
through ``dapr_agents.DurableAgent`` requires the Dapr workflow runtime
(actor placement + scheduler) — those services live on ``spring-net`` per
ADR 0028, but the per-launch daprd sidecar joins the per-workflow bridge
and the tenant network only, so workflow start consistently times out
with ``DaprBuiltInActorNotFoundRetries`` retries on the sidecar. The
agentic loop is implemented inline below using ``DaprChatClient`` + the
MCP tool proxies; durability of in-flight state is the workspace volume
ADR 0029 § "Durable state" defines, not the workflow runtime.

Configuration
-------------
The Spring Voyage Agent SDK reads the canonical env vars at initialize()
time (SPRING_TENANT_ID, SPRING_AGENT_ID, etc. — see
docs/specs/agent-runtime-boundary.md §2.2.1).  The Dapr-specific vars
below are still honoured for backwards-compatibility with existing
deployments:

  SPRING_MODEL          — LLM model name (default: llama3.2:3b). The
                          platform also propagates this into the paired
                          daprd sidecar so llm-*.yaml can resolve it
                          through secretstores.local.env.
  SPRING_LLM_PROVIDER   — Provider type label for telemetry / agent
                          card description (e.g. ``ollama``, ``openai``).
                          The actual Dapr Conversation component name is
                          ``llm-<provider>`` per ADR-0038 (delivered via
                          SPRING_LLM_COMPONENT).
  SPRING_LLM_COMPONENT  — Required: name of the Dapr Conversation
                          component to dial. The Spring Voyage launcher
                          sets this to ``llm-<provider>`` on every
                          dispatch (``llm-anthropic``, ``llm-openai``,
                          ``llm-google``, ``llm-ollama``) per ADR-0038.
                          Missing values raise at ``initialize()``.
  SPRING_AGENT_MAX_STEPS — Maximum tool-call rounds before forcing the
                          loop to terminate (default: 12). Guards
                          against runaway loops without imposing a wall
                          clock that would fight the upstream LLM
                          timeout.
  SPRING_OLLAMA_AUTO_PULL — When provider is ``ollama``, pull the requested
                          SPRING_MODEL at first message time if the local
                          Ollama server does not already have it
                          (default: true).

The platform-assembled system prompt arrives via the SDK bootstrap
bundle (#2734): the SpringVoyageAgentLauncher's ``ContributeBundleAsync``
writes ``$SPRING_WORKSPACE_PATH/.spring/system-prompt.md`` and the SDK's
bootstrap client materialises it before ``initialize()`` runs.  The hook
reads it via ``context.system_prompt`` (spec §2.2.2) — the legacy
``SPRING_SYSTEM_PROMPT`` env-var path was removed at the same time.

  The MCP endpoint and token are read from IAgentContext
  (SPRING_MCP_URL / SPRING_MCP_TOKEN) — the canonical D1-spec names
  emitted by AgentContextBuilder.  The legacy SPRING_MCP_ENDPOINT /
  SPRING_AGENT_TOKEN fallback was removed in #1322.
"""

from __future__ import annotations

import asyncio
import json
import logging
import os
import urllib.error
import urllib.request
from dataclasses import dataclass
from typing import List

from dapr_agents.llm import DaprChatClient
from dapr_agents.tool.base import AgentTool
from dapr_agents.types.message import (
    AssistantMessage,
    BaseMessage,
    SystemMessage,
    ToolMessage,
    UserMessage,
)
from spring_voyage_agent_sdk import (
    IAgentContext,
    Message,
    Response,
    RuntimeContext,
    ShutdownReason,
    run,
)

from mcp_bridge import create_tool_proxy, discover_tools

logging.basicConfig(
    level=logging.INFO,
    format="%(asctime)s %(levelname)s [%(name)s] %(message)s",
)
logger = logging.getLogger("dapr-agent")

# SPRING_LLM_COMPONENT is set on every dispatch by the Spring Voyage
# launcher (SpringVoyageAgentLauncher) — there is no silent fallback.
# Per ADR-0038, in-tree Dapr component files follow the `llm-<provider>`
# naming convention (llm-anthropic, llm-openai, llm-google, llm-ollama).
DEFAULT_MAX_STEPS = 12

# Module-level cache populated by initialize(); on_message reads it.
_agent_build: "AgentBuild | None" = None
# Module-level reference to the IAgentContext from initialize().  on_message
# uses it to resolve the per-thread workspace path (ADR-0041).
_agent_context: IAgentContext | None = None
_ollama_model_lock = asyncio.Lock()


@dataclass
class AgentBuild:
    """Cached agent runtime — the LLM client + the resolved tool list.

    #2734: ``system_prompt`` is NOT cached here. It is re-read from
    ``IAgentContext.system_prompt`` (which reads
    ``$SPRING_WORKSPACE_PATH/.spring/system-prompt.md`` from disk) on
    every ``on_message`` so the SDK's per-turn bootstrap integrity
    check (ADR-0055 §6) is observable — operator edits to the agent
    definition take effect at turn cadence.
    """

    llm: DaprChatClient
    tools: List[AgentTool]
    tools_by_name: dict[str, AgentTool]
    provider: str = ""
    model: str = ""
    llm_provider_url: str = ""
    ollama_auto_pull: bool = True
    ollama_model_ready: bool = False


# ---------------------------------------------------------------------------
# Lifecycle hook: initialize
# ---------------------------------------------------------------------------


async def initialize(context: IAgentContext) -> None:
    """Wire up the Dapr LLM client and discover MCP tools.

    SDK spec §1.1: called exactly once before any on_message invocation.
    The result is cached in the module-level ``_agent_build``.
    """
    global _agent_build, _agent_context

    # Stash the context so on_message can resolve per-thread workspaces
    # via context.thread_workspace(thread_id) — see ADR-0041.
    _agent_context = context

    # MCP endpoint: read from IAgentContext (SPRING_MCP_URL / SPRING_MCP_TOKEN).
    # The legacy SPRING_MCP_ENDPOINT / SPRING_AGENT_TOKEN fallback was removed
    # in #1322 — AgentContextBuilder always emits the canonical D1-spec names.
    mcp_endpoint = context.mcp_url
    mcp_token = context.mcp_token

    model = os.environ.get("SPRING_MODEL", "llama3.2:3b")
    provider = os.environ.get("SPRING_LLM_PROVIDER", "ollama")
    llm_provider_url = _string_attr(context, "llm_provider_url") or os.environ.get("SPRING_LLM_PROVIDER_URL", "")
    component_name = os.environ.get("SPRING_LLM_COMPONENT")
    if not component_name:
        # The launcher always sets SPRING_LLM_COMPONENT to `llm-<provider>`
        # (ADR-0038) so the agent dials the right Dapr Conversation YAML.
        # A missing value here means a misconfigured dispatch — fail loudly
        # instead of silently routing to a default component.
        raise RuntimeError(
            "SPRING_LLM_COMPONENT is not set; the dispatcher must pin a "
            "Dapr Conversation component name (typically "
            "`llm-<provider>` per ADR-0038). This is a launcher bug."
        )

    llm_client = DaprChatClient(component_name=component_name)
    tools: list[AgentTool] = []

    if mcp_endpoint and mcp_token:
        try:
            tool_defs = await discover_tools(mcp_endpoint, mcp_token)
            for td in tool_defs:
                proxy = create_tool_proxy(td, mcp_endpoint, mcp_token)
                tools.append(proxy)
            logger.info("Loaded %d MCP tool proxies", len(tools))
        except Exception:
            logger.exception(
                "Failed to discover MCP tools; continuing without tools",
            )
    else:
        logger.warning(
            "MCP endpoint or token not set; running without MCP tools",
        )

    tools_by_name = {_resolve_tool_name(t, f"tool-{i}"): t for i, t in enumerate(tools)}

    logger.info(
        "Dapr Agent initialized (workflow-free per ADR 0029 Stage 0): provider=%s, model=%s, component=%s, tools=%d",
        provider,
        model,
        component_name,
        len(tools),
    )

    _agent_build = AgentBuild(
        llm=llm_client,
        tools=tools,
        tools_by_name=tools_by_name,
        provider=provider,
        model=model,
        llm_provider_url=llm_provider_url,
        ollama_auto_pull=_env_bool("SPRING_OLLAMA_AUTO_PULL", default=True),
    )


# ---------------------------------------------------------------------------
# Lifecycle hook: on_message
# ---------------------------------------------------------------------------


async def on_message(message: Message):
    """Run the Dapr agentic loop for one inbound A2A message.

    SDK spec §1.2: called once per inbound message; yields Response chunks.
    The tool-calling loop runs against DaprChatClient (Dapr Conversation
    building block) and the MCP tool proxies wired up in initialize().

    Per ADR-0041, on-disk per-thread state (here, a turn counter) lives
    under ``$SPRING_WORKSPACE_PATH/threads/<thread.id>/`` via
    ``IAgentContext.thread_workspace``.  This is the canonical location
    for thread-local files and is safe under both ``concurrent_threads``
    modes.
    """
    assert _agent_build is not None, "initialize() must complete before on_message"

    user_text = message.text or ""

    # Maintain a tiny per-thread turn counter on disk to demonstrate the
    # ADR-0041 per-thread workspace convention.  Real agents will store
    # transcripts, vector caches, scratchpads, etc. here — anything that
    # needs to survive a container restart but be scoped to one thread.
    # _agent_context may be None in unit tests that exercise on_message
    # without going through initialize(); the demonstration is best-effort.
    if _agent_context is not None and message.thread_id:
        thread_dir = _agent_context.thread_workspace(message.thread_id)
        counter_file = thread_dir / "turn-count.txt"
        prev = int(counter_file.read_text()) if counter_file.exists() else 0
        counter_file.write_text(str(prev + 1))
        logger.info(
            "Thread %s turn %d (workspace=%s)",
            message.thread_id,
            prev + 1,
            thread_dir,
        )

    rt = RuntimeContext.current()
    rt.report_progress(
        f"received message on thread {message.thread_id}",
        kind="turn_start",
    )

    # #2734: read the platform-assembled system prompt fresh from
    # IAgentContext on every turn. The SDK has just run the per-turn
    # bootstrap integrity check (ADR-0055 §6) so disk reflects the
    # latest bundle; context.system_prompt is a property that reads
    # the file each access, so this picks up operator edits at turn
    # cadence without a container restart. Falls back to a neutral
    # default when no bundle delivered a prompt (smoke-test harness
    # or alternate launcher).
    system_prompt = (
        _agent_context.system_prompt
        if _agent_context is not None and _agent_context.system_prompt
        else "You are a helpful AI assistant."
    )

    try:
        await _ensure_configured_model(_agent_build)
        result_text = await _run_agentic_loop(_agent_build, system_prompt, user_text, rt)
        if not result_text:
            # An empty result means the loop exited without producing any
            # visible text — typically because the LLM returned an empty
            # assistant message with no tool calls (small models like
            # llama3.2:3b sometimes do this when overwhelmed by a large
            # tool catalog) or because max-steps exhausted with a blank
            # last assistant. We surface this as an explicit error event
            # so the platform records a failure rather than silently
            # completing the task with no artifact.
            logger.error(
                "Agent loop returned empty result for message %s thread %s — "
                "no content and no tool calls. Surfacing as error event.",
                message.message_id,
                message.thread_id,
            )
            rt.report_progress("agent loop returned empty result", kind="turn_failed")
            yield Response(error=("Agent produced an empty response (LLM returned no content and no tool calls)."))
            return
        logger.info(
            "Agent loop completed for message %s thread %s (result_chars=%d)",
            message.message_id,
            message.thread_id,
            len(result_text),
        )
        rt.report_progress(
            f"turn complete ({len(result_text)} chars in reply)",
            kind="turn_complete",
        )
        yield Response(text=result_text, final=True)
    except Exception as exc:
        logger.exception("Agent loop failed for message %s", message.message_id)
        rt.report_progress(f"agent loop failed: {exc}", kind="turn_failed")
        yield Response(error=f"Agent loop failed: {exc}")


# ---------------------------------------------------------------------------
# Lifecycle hook: on_shutdown
# ---------------------------------------------------------------------------


async def on_shutdown(reason: ShutdownReason) -> None:
    """Flush in-progress state on SIGTERM.

    SDK spec §1.3: called exactly once on container termination.
    The Dapr-based agent stores its durable state in the workspace volume
    (SPRING_WORKSPACE_PATH) rather than in memory, so the shutdown hook
    is minimal: close any open resources and log the reason.
    """
    logger.info("Dapr Agent shutting down (reason=%s)", reason.value)
    # Tool proxies use ephemeral httpx.AsyncClient instances (one per call);
    # there are no persistent connections to drain here.


# ---------------------------------------------------------------------------
# Internal helpers (unchanged from the pre-SDK agent)
# ---------------------------------------------------------------------------


async def _run_agentic_loop(
    build: AgentBuild,
    system_prompt: str,
    user_text: str,
    rt: RuntimeContext | None = None,
) -> str:
    """Run a tool-calling loop until the LLM returns a final assistant
    message (no further tool calls) or ``SPRING_AGENT_MAX_STEPS`` rounds
    elapse. Returns the assistant's final textual response.

    ``system_prompt`` is supplied per-call rather than stashed on
    ``build`` so each turn picks up the freshest platform-assembled
    prompt (#2734): on_message reads ``IAgentContext.system_prompt``
    right after the SDK's per-turn bootstrap integrity check (ADR-0055
    §6) and threads it in here.

    Each iteration is one Conversation API call to daprd (a single unary
    gRPC, no workflow / actor placement involvement) plus, if the model
    asked for tools, one HTTP call per tool invocation against the
    Spring MCP server.

    When invoked through the SDK runtime (issue #2493) ``rt`` carries
    the active :class:`RuntimeContext` so the loop can emit telemetry
    spans for each LLM turn and tool call. Called from unit tests
    without an SDK context, ``rt`` may be ``None`` — every emission is
    then a no-op via :meth:`RuntimeContext.current`.
    """
    max_steps = int(os.environ.get("SPRING_AGENT_MAX_STEPS", str(DEFAULT_MAX_STEPS)))
    rt = rt or RuntimeContext.current()

    messages: List[BaseMessage] = []
    if system_prompt:
        messages.append(SystemMessage(content=system_prompt))
    messages.append(UserMessage(content=user_text))

    loop = asyncio.get_event_loop()

    for step in range(max_steps):
        logger.info(
            "Agent loop step %d/%d (messages=%d, tools=%d)",
            step + 1,
            max_steps,
            len(messages),
            len(build.tools),
        )
        rt.report_progress(f"LLM turn {step + 1}/{max_steps}", kind="llm_turn_start")
        # DaprChatClient.generate is synchronous and blocks until daprd
        # returns the Conversation call's response — which is the full LLM
        # turn (Ollama on a slow host can take tens of seconds). Running it
        # directly inside `async def on_message` blocks the asyncio event
        # loop, which freezes uvicorn's request handling for the whole
        # turn. That includes `GET /.well-known/agent.json`, which the
        # platform's persistent-agent health probe relies on; three
        # consecutive timeouts trip the restart threshold and kill the
        # container mid-inference (issue #2159).
        #
        # Off-load the blocking call to the default executor so the event
        # loop stays responsive and the health probe continues to answer
        # 200 while the LLM call is in flight.
        step_started = loop.time()
        try:
            async with rt.llm_turn(build.model, prompt=None) as llm_span:
                response = await asyncio.to_thread(
                    build.llm.generate,
                    messages=messages,
                    tools=build.tools,
                )
                # The dapr-agents response shape doesn't expose token
                # counts in a normalised form, so we record only what's
                # available without making the call site brittle.
                llm_span.set_completion(None)
        except Exception:
            logger.exception(
                "LLM generate() raised at step %d/%d after %.1fs",
                step + 1,
                max_steps,
                loop.time() - step_started,
            )
            raise

        step_elapsed = loop.time() - step_started
        assistant = response.get_message()
        if assistant is None:
            logger.error(
                "Step %d/%d returned no candidate message after %.1fs — "
                "Dapr Conversation component or upstream LLM failed silently.",
                step + 1,
                max_steps,
                step_elapsed,
            )
            raise RuntimeError(
                "LLM returned no candidate message — check Conversation component health.",
            )

        tool_calls = getattr(assistant, "tool_calls", None) or []
        content = assistant.content or ""
        logger.info(
            "Step %d/%d LLM responded in %.1fs (content_chars=%d, tool_calls=%d)",
            step + 1,
            max_steps,
            step_elapsed,
            len(content),
            len(tool_calls),
        )

        messages.append(assistant)

        if not tool_calls:
            if not content:
                logger.error(
                    "Step %d/%d: LLM produced no content AND no tool calls. "
                    "This is a malformed assistant turn — surfacing as error "
                    "rather than silently returning an empty response.",
                    step + 1,
                    max_steps,
                )
                raise RuntimeError(
                    f"LLM returned empty assistant message at step {step + 1} "
                    "(no content and no tool calls). The model could not "
                    "make progress with the current prompt or tool set.",
                )
            logger.info(
                "Step %d/%d: terminal assistant response (%d chars)",
                step + 1,
                max_steps,
                len(content),
            )
            return content

        # Run the requested tools sequentially. Errors per-tool are
        # surfaced back to the model so it can re-plan or apologise; we
        # never abort the whole turn on a single tool failure, but every
        # failure path emits a log so silence cannot hide a stuck call.
        for tool_call in tool_calls:
            tool_name = tool_call.function.name
            tool_started = loop.time()
            try:
                tool_args = json.loads(tool_call.function.arguments or "{}")
            except json.JSONDecodeError as exc:
                tool_result_text = f"Failed to decode tool arguments JSON for '{tool_name}': {exc}"
                logger.warning(
                    "Step %d/%d tool '%s': JSON decode failed: %s",
                    step + 1,
                    max_steps,
                    tool_name,
                    exc,
                )
                messages.append(
                    ToolMessage(
                        content=tool_result_text,
                        tool_call_id=tool_call.id,
                    )
                )
                continue

            tool = build.tools_by_name.get(tool_name)
            if tool is None:
                tool_result_text = f"Tool '{tool_name}' is not registered with this agent."
                logger.warning(
                    "Step %d/%d tool '%s' is not registered (model hallucinated a name)",
                    step + 1,
                    max_steps,
                    tool_name,
                )
                messages.append(
                    ToolMessage(
                        content=tool_result_text,
                        tool_call_id=tool_call.id,
                    )
                )
                continue

            try:
                async with rt.tool_call(tool_name, tool_args) as tool_span:
                    tool_result = await tool.arun(**tool_args)
                    tool_elapsed = loop.time() - tool_started
                    result_repr = str(tool_result) if tool_result is not None else ""
                    tool_span.set_result(tool_result)
                    logger.info(
                        "Step %d/%d tool '%s' returned in %.2fs (result_chars=%d)",
                        step + 1,
                        max_steps,
                        tool_name,
                        tool_elapsed,
                        len(result_repr),
                    )
            except Exception as exc:
                tool_elapsed = loop.time() - tool_started
                logger.exception(
                    "Step %d/%d tool '%s' raised after %.2fs",
                    step + 1,
                    max_steps,
                    tool_name,
                    tool_elapsed,
                )
                rt.report_progress(
                    f"tool '{tool_name}' raised after {tool_elapsed:.2f}s: {exc}",
                    kind="tool_failed",
                )
                tool_result = f"Error invoking tool '{tool_name}': {exc}"

            messages.append(
                ToolMessage(
                    content=str(tool_result) if tool_result is not None else "",
                    tool_call_id=tool_call.id,
                )
            )

    # Loop budget exhausted without a terminal assistant message. We
    # surface this as a structured error rather than returning the
    # last (possibly blank) assistant content as a clean result. The
    # last-assistant text, if any, is included in the error so the
    # caller can still see what the model produced before giving up.
    last_assistant = next(
        (m for m in reversed(messages) if isinstance(m, AssistantMessage)),
        None,
    )
    fallback = (last_assistant.content if last_assistant else "") or ""
    logger.error(
        "Agent loop exhausted %d steps without a terminal response (last_assistant_chars=%d). Surfacing as error.",
        max_steps,
        len(fallback),
    )
    if fallback:
        raise RuntimeError(
            f"Agent loop exhausted {max_steps} steps without producing a "
            f"terminal response. Last assistant content: {fallback!r}"
        )
    raise RuntimeError(f"Agent loop exhausted {max_steps} steps without producing any assistant content.")


async def _ensure_configured_model(build: AgentBuild) -> None:
    """Perform provider-specific model readiness checks before the first turn."""
    if build.provider.lower() != "ollama" or not build.ollama_auto_pull or build.ollama_model_ready:
        return

    async with _ollama_model_lock:
        if build.ollama_model_ready:
            return

        await _ensure_ollama_model(build.model, build.llm_provider_url)
        build.ollama_model_ready = True


async def _ensure_ollama_model(model: str, provider_url: str) -> None:
    """Ensure the requested Ollama model exists, pulling it on demand."""
    if not model:
        raise RuntimeError("SPRING_MODEL is empty; cannot request an Ollama model.")

    base_url = _ollama_api_base_url(provider_url)
    if not base_url:
        raise RuntimeError("SPRING_LLM_PROVIDER_URL is empty; cannot check or pull the requested Ollama model.")

    await asyncio.to_thread(_ensure_ollama_model_sync, model, base_url)


def _ensure_ollama_model_sync(model: str, base_url: str) -> None:
    show_timeout = _env_int("SPRING_OLLAMA_SHOW_TIMEOUT_SECONDS", default=10)
    pull_timeout = _env_int("SPRING_OLLAMA_PULL_TIMEOUT_SECONDS", default=1800)

    try:
        _post_ollama_json(base_url, "/api/show", {"model": model}, timeout_seconds=show_timeout)
        logger.info("Ollama model %s is already available at %s", model, base_url)
        return
    except urllib.error.HTTPError as exc:
        if exc.code != 404:
            raise RuntimeError(
                f"Ollama model check failed for {model!r} at {base_url}: {_http_error_summary(exc)}"
            ) from exc
    except urllib.error.URLError as exc:
        raise RuntimeError(f"Could not reach Ollama at {base_url} to check model {model!r}: {exc.reason}") from exc
    except TimeoutError as exc:
        raise RuntimeError(f"Timed out checking Ollama model {model!r} at {base_url}.") from exc

    logger.info("Ollama model %s is not installed at %s; pulling it now", model, base_url)
    try:
        _post_ollama_json(base_url, "/api/pull", {"model": model, "stream": False}, timeout_seconds=pull_timeout)
    except urllib.error.HTTPError as exc:
        raise RuntimeError(
            f"Ollama model {model!r} is not installed and automatic pull failed: {_http_error_summary(exc)}. "
            "Verify the model name, or pull/create it on the Ollama host."
        ) from exc
    except urllib.error.URLError as exc:
        raise RuntimeError(
            f"Ollama model {model!r} is not installed and automatic pull could not reach {base_url}: {exc.reason}"
        ) from exc
    except TimeoutError as exc:
        raise RuntimeError(
            f"Timed out pulling Ollama model {model!r} from {base_url}. "
            "The pull may still be running on the Ollama host; retry the agent turn after it completes."
        ) from exc

    logger.info("Ollama model %s was pulled successfully from %s", model, base_url)


def _post_ollama_json(base_url: str, path: str, payload: dict, *, timeout_seconds: int) -> str:
    data = json.dumps(payload).encode("utf-8")
    request = urllib.request.Request(
        f"{base_url}{path}",
        data=data,
        headers={"Content-Type": "application/json"},
        method="POST",
    )
    with urllib.request.urlopen(request, timeout=timeout_seconds) as response:
        return response.read().decode("utf-8", errors="replace")


def _ollama_api_base_url(provider_url: str) -> str:
    base_url = provider_url.strip().rstrip("/")
    if base_url.endswith("/v1"):
        base_url = base_url[: -len("/v1")]
    return base_url.rstrip("/")


def _http_error_summary(exc: urllib.error.HTTPError) -> str:
    body = exc.read().decode("utf-8", errors="replace").strip()
    if body:
        return f"HTTP {exc.code}: {body}"
    return f"HTTP {exc.code}"


def _env_bool(name: str, *, default: bool) -> bool:
    value = os.environ.get(name)
    if value is None:
        return default
    return value.strip().lower() not in {"0", "false", "no", "off"}


def _env_int(name: str, *, default: int) -> int:
    value = os.environ.get(name)
    if value is None:
        return default
    try:
        parsed = int(value)
    except ValueError:
        logger.warning("Ignoring invalid integer env var %s=%r; using %d", name, value, default)
        return default
    return parsed if parsed > 0 else default


def _string_attr(obj: object, name: str) -> str:
    value = getattr(obj, name, "")
    return value if isinstance(value, str) else ""


def _resolve_tool_name(tool: AgentTool, fallback: str) -> str:
    """Pick the canonical name the LLM tool-call will reference."""
    return getattr(tool, "name", None) or fallback


# ---------------------------------------------------------------------------
# Entry point
# ---------------------------------------------------------------------------


def main() -> None:
    """Start the Dapr Agent via the Spring Voyage Agent SDK runtime."""
    run(
        initialize=initialize,
        on_message=on_message,
        on_shutdown=on_shutdown,
    )


if __name__ == "__main__":
    main()
