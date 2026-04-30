"""
AgentRuntime — SDK runtime that wires the three lifecycle hooks to the
A2A server, SIGTERM, and the concurrent-thread scheduler.

Implements the full Bucket 1 contract:
  - initialize() runs before any on_message (spec §1.1)
  - on_message() is called once per inbound A2A message (spec §1.2)
  - per-thread FIFO preserved (spec §1.2.3)
  - concurrent_threads flag honoured (spec §1.2.4)
  - on_shutdown() called on SIGTERM within grace window (spec §1.3)
  - SIGTERM trapped; SDK calls on_shutdown synchronously (spec §1.3)

The runtime wraps the a2a-sdk 1.x server so agent authors implement
only the three hooks, not A2A protocol details.

a2a-sdk 1.x migration (issue #940)
-----------------------------------
A2AStarletteApplication was removed in 1.0.  The equivalent is a plain
Starlette application composed from create_rest_routes() +
create_agent_card_routes().  DefaultRequestHandler now requires agent_card
and queue_manager.  TaskState, TaskStatus, TaskStatusUpdateEvent, and
TaskArtifactUpdateEvent are protobuf types; the helpers new_text_artifact,
new_agent_text_message, and new_task are gone — use TaskUpdater instead.

Startup model (uvicorn-first)
-----------------------------
The A2A server (uvicorn) binds and begins serving the agent card
*before* IAgentContext.load() is attempted.  Context loading and
initialize() run concurrently with the server in a background task.

This means the /.well-known/agent.json endpoint is reachable even in
environments where the platform bootstrap env vars are absent (e.g. the
smoke-test harness).  If context loading fails, initialize() is never
called and _initialize_done is never set — the agent card stays
reachable but on_message will block indefinitely (the gate never
opens).  In production the platform always injects the required env
vars before container start, so this path is never exercised.
"""

from __future__ import annotations

import asyncio
import logging
import os
import signal
import sys
from typing import Any, Callable

import uvicorn
from a2a.server.agent_execution import AgentExecutor, RequestContext
from a2a.server.events import EventQueue
from a2a.server.events.in_memory_queue_manager import InMemoryQueueManager
from a2a.server.request_handlers import DefaultRequestHandler
from a2a.server.routes import create_agent_card_routes, create_rest_routes
from a2a.server.tasks import InMemoryTaskStore
from a2a.server.tasks.task_updater import TaskUpdater
from a2a.types import (
    AgentCapabilities,
    AgentCard,
    AgentSkill,
)
from a2a.types.a2a_pb2 import AgentInterface, Part
from starlette.applications import Starlette

from spring_voyage_agent.context import IAgentContext
from spring_voyage_agent.hooks import AgentHooks
from spring_voyage_agent.types import Message, Response, Sender, ShutdownReason

logger = logging.getLogger("spring-voyage-agent.runtime")

_DEFAULT_PORT = 8999
_INIT_TIMEOUT_SECONDS = 30
_SHUTDOWN_GRACE_SECONDS = 30


def _extract_text_from_parts(parts: Any) -> str:
    """Extract plain text from a sequence of a2a-sdk 1.x Part objects.

    In a2a-sdk 1.x Parts are protobuf messages with a ``content`` oneof
    (text | raw | url | data).  We collect all text-typed parts and join them
    with a newline separator, which mirrors the de-facto convention used by
    all current callers.
    """
    fragments: list[str] = []
    for part in parts:
        if part.WhichOneof("content") == "text" and part.text:
            fragments.append(part.text)
    return "\n".join(fragments)


def _build_message_from_a2a(ctx: RequestContext) -> Message:
    """Convert an a2a-sdk 1.x RequestContext into a SDK Message.

    In a2a-sdk 1.x ``context.message`` is a protobuf ``Message``; parts are
    protobuf ``Part`` objects with a ``content`` oneof (text/raw/url/data).
    """
    task_id = ctx.task_id or ""
    context_id = ctx.context_id or ""

    # Reconstruct the raw A2A payload from the request message so that the
    # Spring Voyage agent SDK's Message.text helper can read it.
    raw_payload: dict[str, Any] = {}
    if ctx.message:
        raw_parts: list[Any] = []
        for part in ctx.message.parts:
            # Keep raw proto parts; Message.text handles both dict and proto shapes.
            raw_parts.append(part)
        raw_payload = {
            "role": "user",
            "parts": raw_parts,
        }

    sender = Sender(
        kind="human",
        id=context_id or "unknown",
        display_name=None,
    )

    return Message(
        thread_id=context_id,
        message_id=task_id,
        sender=sender,
        payload=raw_payload,
        timestamp="",
        pending_count=0,
        context=None,
    )


class _SdkAgentExecutor(AgentExecutor):
    """A2A AgentExecutor that bridges on_message into the SDK hook.

    One executor instance is created per AgentRuntime. It honours the
    concurrent_threads flag by serialising invocations when the flag is False.

    The per-thread FIFO invariant (spec §1.2.3) is maintained by the A2A
    server's InMemoryTaskStore, which sequences tasks per context_id (the
    platform's thread_id equivalent). We enforce the concurrent_threads=False
    global serialisation with an asyncio.Lock.
    """

    def __init__(
        self,
        hooks: AgentHooks,
        concurrent_threads: bool,
        initialize_done: asyncio.Event,
    ) -> None:
        self._hooks = hooks
        self._concurrent_threads = concurrent_threads
        self._initialize_done = initialize_done
        # Global lock for concurrent_threads=False serialisation.
        self._serial_lock: asyncio.Lock | None = None if concurrent_threads else asyncio.Lock()

    async def execute(self, context: RequestContext, event_queue: EventQueue) -> None:
        """Run on_message for one inbound A2A task."""
        task_id = context.task_id or ""
        context_id = context.context_id or ""

        updater = TaskUpdater(event_queue, task_id, context_id)
        await updater.submit()

        # Spec §1.1: on_message MUST NOT run before initialize completes.
        await self._initialize_done.wait()

        await updater.start_work()

        try:
            if self._serial_lock is not None:
                async with self._serial_lock:
                    await self._run_on_message(context, updater)
            else:
                await self._run_on_message(context, updater)
        except Exception as exc:
            logger.exception("on_message hook raised an unhandled exception")
            await updater.failed(updater.new_agent_message([Part(text=f"Agent error: {exc}")]))

    async def _run_on_message(self, context: RequestContext, updater: TaskUpdater) -> None:
        """Invoke the on_message hook and stream its responses.

        Supports both async generators (``async def on_message`` that yields)
        and regular coroutines (``async def on_message`` that returns a value).
        """
        message = _build_message_from_a2a(context)

        result = self._hooks.on_message(message)

        # Collect text fragments for the final artifact.
        text_chunks: list[str] = []
        error_text: str | None = None

        if hasattr(result, "__aiter__"):
            # Async generator / async iterable path.
            async for chunk in result:
                response: Response = chunk
                if response.error:
                    error_text = response.error
                    break
                if response.text:
                    text_chunks.append(response.text)
        elif asyncio.iscoroutine(result):
            # Plain coroutine that returns a single value.
            value = await result
            if isinstance(value, Response):
                if value.error:
                    error_text = value.error
                elif value.text:
                    text_chunks.append(value.text)
            elif value is not None:
                text_chunks.append(str(value))
        else:
            # Sync iterable — run in executor to avoid blocking the event loop.
            loop = asyncio.get_event_loop()
            items = await loop.run_in_executor(None, list, result)  # type: ignore[arg-type]
            for chunk in items:
                response = chunk
                if response.error:
                    error_text = response.error
                    break
                if response.text:
                    text_chunks.append(response.text)

        if error_text is not None:
            await updater.failed(updater.new_agent_message([Part(text=error_text)]))
            return

        full_text = "".join(text_chunks)
        if full_text:
            await updater.add_artifact(
                parts=[Part(text=full_text)],
                name="response",
            )

        await updater.complete()

    async def cancel(self, context: RequestContext, event_queue: EventQueue) -> None:
        """Cancel a running task."""
        task_id = context.task_id or ""
        context_id = context.context_id or ""
        updater = TaskUpdater(event_queue, task_id, context_id)
        await updater.cancel(updater.new_agent_message([Part(text="Task canceled.")]))


def _build_agent_card(port: int) -> AgentCard:
    """Build a minimal A2A Agent Card from IAgentContext env vars."""
    agent_id = os.environ.get("SPRING_AGENT_ID", "agent")
    tenant_id = os.environ.get("SPRING_TENANT_ID", "tenant")

    skill = AgentSkill(
        id=f"{agent_id}-execute",
        name="Execute",
        description=f"Spring Voyage agent {agent_id} (tenant {tenant_id}).",
        tags=["spring-voyage", agent_id],
        examples=[],
    )

    return AgentCard(
        name=f"Spring Voyage Agent — {agent_id}",
        description=(f"Agent {agent_id} running on tenant {tenant_id}. Powered by the Spring Voyage Agent SDK."),
        # In a2a-sdk 1.x the agent URL lives in supported_interfaces.
        supported_interfaces=[AgentInterface(url=f"http://localhost:{port}")],
        version="1.0.0",
        default_input_modes=["text"],
        default_output_modes=["text"],
        capabilities=AgentCapabilities(streaming=True),
        skills=[skill],
    )


class AgentRuntime:
    """SDK runtime — wires three hooks to the A2A server and SIGTERM.

    Lifecycle:
      1. ``run()`` called.
      2. ``initialize(context)`` called; A2A server is bound but not serving
         on_message until initialize completes.
      3. A2A server begins accepting on_message invocations.
      4. On SIGTERM: ``on_shutdown(reason)`` called; server stops.

    Spec: docs/specs/agent-runtime-boundary.md §1.
    """

    def __init__(
        self,
        hooks: AgentHooks,
        *,
        port: int | None = None,
        init_timeout: float = _INIT_TIMEOUT_SECONDS,
        shutdown_grace: float = _SHUTDOWN_GRACE_SECONDS,
    ) -> None:
        self._hooks = hooks
        self._port = port or int(os.environ.get("AGENT_PORT", str(_DEFAULT_PORT)))
        self._init_timeout = init_timeout
        self._shutdown_grace = shutdown_grace

        # Event set by initialize() completion; on_message waits on it.
        self._initialize_done = asyncio.Event()
        # Set when SIGTERM arrives; drives the shutdown path.
        self._shutdown_requested = asyncio.Event()
        self._shutdown_reason = ShutdownReason.unknown

    async def _run_initialize(self, context: IAgentContext) -> None:
        """Run the initialize hook with a timeout.

        Spec §1.1: completes in ≤30 s or the platform MAY abort.
        """
        try:
            await asyncio.wait_for(
                self._hooks.initialize(context),
                timeout=self._init_timeout,
            )
        except asyncio.TimeoutError:
            raise RuntimeError(
                f"initialize() did not complete within {self._init_timeout}s "
                "(spec §1.1 requires completion within 30 s)"
            )
        finally:
            # Signal on_message regardless of outcome so the executor can
            # report errors rather than hanging indefinitely.
            self._initialize_done.set()

    def _install_sigterm_handler(self, loop: asyncio.AbstractEventLoop) -> None:
        """Install a SIGTERM handler that sets the shutdown event.

        Spec §1.3: the SDK MUST trap SIGTERM.
        """

        def _handle_sigterm() -> None:
            logger.info("SIGTERM received — initiating graceful shutdown")
            self._shutdown_reason = ShutdownReason.requested
            loop.call_soon_threadsafe(self._shutdown_requested.set)

        loop.add_signal_handler(signal.SIGTERM, _handle_sigterm)

    async def _run_shutdown(self) -> None:
        """Wait for SIGTERM then call on_shutdown within the grace window."""
        await self._shutdown_requested.wait()
        logger.info("Calling on_shutdown(reason=%s)", self._shutdown_reason.value)
        try:
            await asyncio.wait_for(
                self._hooks.on_shutdown(self._shutdown_reason),
                timeout=self._shutdown_grace,
            )
        except asyncio.TimeoutError:
            logger.warning(
                "on_shutdown() did not complete within %ss grace window — platform may SIGKILL",
                self._shutdown_grace,
            )

    async def _load_and_initialize(self, executor: "_SdkAgentExecutor") -> None:
        """Load IAgentContext then run the initialize hook in the background.

        This runs concurrently with the uvicorn server so the A2A server
        can serve the agent card even before the platform context is
        available (e.g. in the smoke-test harness with no env vars).

        If context loading fails the error is logged and _initialize_done
        is left unset — the agent card stays reachable but on_message will
        block indefinitely until a shutdown signal arrives.

        If initialize() succeeds, _initialize_done is set and on_message
        invocations are unblocked.  If initialize() raises or times out,
        _initialize_done is still set (by _run_initialize's finally block)
        so that executors can surface an error rather than hanging.
        """
        try:
            context = IAgentContext.load()
        except Exception as exc:
            logger.warning(
                "IAgentContext.load() failed (%s) — agent card will be reachable "
                "but on_message will not be dispatched until context is available. "
                "In production the platform always injects the required env vars.",
                exc,
            )
            # _initialize_done is never set here; on_message blocks.
            return

        # Update the executor's concurrent_threads policy now that we know
        # the platform-supplied value.  We do this before calling initialize
        # so that any message that squeaks in before initialize completes
        # still gets the right serialisation behaviour.
        if not context.concurrent_threads and executor._serial_lock is None:
            executor._serial_lock = asyncio.Lock()

        await self._run_initialize(context)
        logger.info("initialize() completed; on_message now dispatching on port %d", self._port)

    async def _serve(self) -> None:
        """Bind the A2A server, load context in the background, then shut down.

        Uvicorn binds first so the /.well-known/agent.json endpoint is
        reachable immediately — even when the platform bootstrap env vars
        are not present (smoke-test harness).  IAgentContext.load() and
        initialize() run concurrently in a background task.
        """
        loop = asyncio.get_running_loop()
        self._install_sigterm_handler(loop)

        # Build A2A server components.  concurrent_threads defaults to True;
        # _load_and_initialize will add the serial lock if the platform
        # context specifies concurrent_threads=False.
        executor = _SdkAgentExecutor(
            hooks=self._hooks,
            concurrent_threads=True,
            initialize_done=self._initialize_done,
        )
        card = _build_agent_card(self._port)
        handler = DefaultRequestHandler(
            agent_executor=executor,
            task_store=InMemoryTaskStore(),
            agent_card=card,
            queue_manager=InMemoryQueueManager(),
        )
        # In a2a-sdk 1.x A2AStarletteApplication is gone; compose a plain
        # Starlette application from the A2A route builders.
        # Order matters. create_rest_routes registers a /{tenant}/{path:.*} mount
        # that matches every two-segment path — including
        # /.well-known/agent-card.json and /.well-known/agent.json — so the
        # agent-card routes MUST be registered first. The second
        # create_agent_card_routes call adds the legacy /.well-known/agent.json
        # alias the smoke contract and existing consumers expect alongside the
        # SDK 1.x canonical /.well-known/agent-card.json path.
        routes = (
            create_agent_card_routes(card)
            + create_agent_card_routes(card, card_url="/.well-known/agent.json")
            + create_rest_routes(handler)
        )
        app = Starlette(routes=routes)

        # Run uvicorn until SIGTERM arrives.
        config = uvicorn.Config(
            app=app,
            host="0.0.0.0",
            port=self._port,
            log_config=None,
        )
        server = uvicorn.Server(config)

        # Run server, shutdown watcher, and context-loading concurrently.
        server_task = asyncio.create_task(server.serve())
        shutdown_task = asyncio.create_task(self._run_shutdown())
        init_task = asyncio.create_task(self._load_and_initialize(executor))

        done, pending = await asyncio.wait(
            {server_task, shutdown_task},
            return_when=asyncio.FIRST_COMPLETED,
        )

        # If shutdown arrived first, stop the server.
        if shutdown_task in done:
            server.should_exit = True
            await server_task

        # Cancel remaining tasks.
        for t in pending | {init_task}:
            t.cancel()
            try:
                await t
            except asyncio.CancelledError:
                pass

    def run(self) -> None:
        """Start the event loop and block until shutdown.

        This is the main entry point for a running agent container.
        IAgentContext.load() is attempted after uvicorn binds (uvicorn-
        first startup model — see module docstring).
        """
        try:
            asyncio.run(self._serve())
        except Exception as exc:
            logger.critical("Fatal runtime error: %s", exc)
            sys.exit(1)


def run(
    *,
    initialize: Callable,
    on_message: Callable,
    on_shutdown: Callable,
    port: int | None = None,
    init_timeout: float = _INIT_TIMEOUT_SECONDS,
    shutdown_grace: float = _SHUTDOWN_GRACE_SECONDS,
) -> None:
    """Entry point for agent authors.

    Constructs :class:`AgentRuntime` from the three lifecycle callables and
    starts the A2A server. Blocks until the container shuts down.

    Parameters
    ----------
    initialize:
        Async callable ``(context: IAgentContext) -> None``. Invoked once
        at container start; must complete within *init_timeout* seconds.
    on_message:
        Async callable or async generator ``(message: Message) -> ...``.
        Invoked once per inbound A2A message; should yield
        :class:`~spring_voyage_agent.types.Response` chunks.
    on_shutdown:
        Async callable ``(reason: ShutdownReason) -> None``. Invoked once
        on SIGTERM; must complete within *shutdown_grace* seconds.
    port:
        A2A server listen port. Defaults to ``AGENT_PORT`` env var or 8999.
    init_timeout:
        Maximum seconds allowed for ``initialize()`` (spec §1.1 default: 30).
    shutdown_grace:
        Grace window in seconds for ``on_shutdown()`` (spec §1.3 default: 30).
    """
    hooks = AgentHooks(
        initialize=initialize,
        on_message=on_message,
        on_shutdown=on_shutdown,
    )
    runtime = AgentRuntime(
        hooks,
        port=port,
        init_timeout=init_timeout,
        shutdown_grace=shutdown_grace,
    )
    runtime.run()
