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

The runtime wraps the a2a-sdk v0.3.x server so agent authors implement
only the three hooks, not A2A protocol details.
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
from a2a.server.apps import A2AStarletteApplication
from a2a.server.events import EventQueue
from a2a.server.request_handlers import DefaultRequestHandler
from a2a.server.tasks import InMemoryTaskStore
from a2a.types import (
    AgentCapabilities,
    AgentCard,
    AgentSkill,
    TaskArtifactUpdateEvent,
    TaskState,
    TaskStatus,
    TaskStatusUpdateEvent,
)
from a2a.utils.artifact import new_text_artifact
from a2a.utils.message import new_agent_text_message
from a2a.utils.task import new_task

from spring_voyage_agent.context import IAgentContext
from spring_voyage_agent.hooks import AgentHooks
from spring_voyage_agent.types import Message, Response, Sender, ShutdownReason

logger = logging.getLogger("spring-voyage-agent.runtime")

_DEFAULT_PORT = 8999
_INIT_TIMEOUT_SECONDS = 30
_SHUTDOWN_GRACE_SECONDS = 30


def _build_message_from_a2a(ctx: RequestContext) -> Message:
    """Convert an a2a-sdk RequestContext into a SDK Message.

    The a2a-sdk v0.3.x ``Part`` shape is a discriminated-union wrapper
    (``Part(root=TextPart|FilePart|DataPart)``); text is read via
    ``part.root.text`` not ``part.text``.
    """
    task_id = ctx.task_id or ""
    context_id = ctx.context_id or ""

    # Reconstruct the raw A2A payload from the request message.
    raw_payload: dict[str, Any] = {}
    if ctx.message:
        raw_parts: list[Any] = []
        if ctx.message.parts:
            for part in ctx.message.parts:
                # Keep the raw part object so Message.text can access it;
                # the Message.text property handles both dict and SDK shapes.
                raw_parts.append(part)
        raw_payload = {
            "role": getattr(ctx.message, "role", "user"),
            "parts": raw_parts,
        }

    # The a2a-sdk does not carry Spring-specific sender/thread metadata on the
    # request context in 0.3.x; we populate from the task/context identifiers
    # the SDK does expose. Full sender resolution requires platform-level
    # enrichment (delivered by the dispatcher before the container receives the
    # A2A call); for now we use what the SDK surface provides.
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
        self._serial_lock: asyncio.Lock | None = (
            None if concurrent_threads else asyncio.Lock()
        )

    async def execute(self, context: RequestContext, event_queue: EventQueue) -> None:
        """Run on_message for one inbound A2A task."""
        task = context.current_task or new_task(context.message)
        await event_queue.enqueue_event(task)

        # Spec §1.1: on_message MUST NOT run before initialize completes.
        await self._initialize_done.wait()

        await event_queue.enqueue_event(
            TaskStatusUpdateEvent(
                task_id=context.task_id,
                context_id=context.context_id,
                final=False,
                status=TaskStatus(
                    state=TaskState.working,
                    message=new_agent_text_message("Processing..."),
                ),
            )
        )

        try:
            if self._serial_lock is not None:
                async with self._serial_lock:
                    await self._run_on_message(context, event_queue)
            else:
                await self._run_on_message(context, event_queue)
        except Exception as exc:
            logger.exception("on_message hook raised an unhandled exception")
            await event_queue.enqueue_event(
                TaskStatusUpdateEvent(
                    task_id=context.task_id,
                    context_id=context.context_id,
                    final=True,
                    status=TaskStatus(
                        state=TaskState.failed,
                        message=new_agent_text_message(f"Agent error: {exc}"),
                    ),
                )
            )

    async def _run_on_message(
        self, context: RequestContext, event_queue: EventQueue
    ) -> None:
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
            await event_queue.enqueue_event(
                TaskStatusUpdateEvent(
                    task_id=context.task_id,
                    context_id=context.context_id,
                    final=True,
                    status=TaskStatus(
                        state=TaskState.failed,
                        message=new_agent_text_message(error_text),
                    ),
                )
            )
            return

        full_text = "".join(text_chunks)
        if full_text:
            await event_queue.enqueue_event(
                TaskArtifactUpdateEvent(
                    task_id=context.task_id,
                    context_id=context.context_id,
                    artifact=new_text_artifact(name="response", text=full_text),
                )
            )

        await event_queue.enqueue_event(
            TaskStatusUpdateEvent(
                task_id=context.task_id,
                context_id=context.context_id,
                final=True,
                status=TaskStatus(state=TaskState.completed),
            )
        )

    async def cancel(self, context: RequestContext, event_queue: EventQueue) -> None:
        """Cancel a running task."""
        await event_queue.enqueue_event(
            TaskStatusUpdateEvent(
                task_id=context.task_id,
                context_id=context.context_id,
                final=True,
                status=TaskStatus(
                    state=TaskState.canceled,
                    message=new_agent_text_message("Task canceled."),
                ),
            )
        )


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
        description=(
            f"Agent {agent_id} running on tenant {tenant_id}. "
            "Powered by the Spring Voyage Agent SDK."
        ),
        url=f"http://localhost:{port}",
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

    async def _serve(self, context: IAgentContext) -> None:
        """Initialize, serve A2A traffic, then shut down."""
        loop = asyncio.get_running_loop()
        self._install_sigterm_handler(loop)

        # Run initialize() — raises on failure (non-zero exit via asyncio).
        await self._run_initialize(context)
        logger.info(
            "initialize() completed; A2A server ready on port %d", self._port
        )

        # Build A2A server components.
        executor = _SdkAgentExecutor(
            hooks=self._hooks,
            concurrent_threads=context.concurrent_threads,
            initialize_done=self._initialize_done,
        )
        card = _build_agent_card(self._port)
        handler = DefaultRequestHandler(
            agent_executor=executor,
            task_store=InMemoryTaskStore(),
        )
        app = A2AStarletteApplication(agent_card=card, http_handler=handler)

        # Run uvicorn until SIGTERM arrives.
        config = uvicorn.Config(
            app=app.build(),
            host="0.0.0.0",
            port=self._port,
            log_config=None,
        )
        server = uvicorn.Server(config)

        # Run the server and shutdown watcher concurrently.
        server_task = asyncio.create_task(server.serve())
        shutdown_task = asyncio.create_task(self._run_shutdown())

        done, pending = await asyncio.wait(
            {server_task, shutdown_task},
            return_when=asyncio.FIRST_COMPLETED,
        )

        # If shutdown arrived first, stop the server.
        if shutdown_task in done:
            server.should_exit = True
            await server_task

        # Cancel the other task if still running.
        for t in pending:
            t.cancel()
            try:
                await t
            except asyncio.CancelledError:
                pass

    def run(self) -> None:
        """Load IAgentContext, start the event loop, and block until shutdown.

        This is the main entry point for a running agent container. It exits
        with a non-zero code on initialize() failure (spec §1.1).
        """
        try:
            context = IAgentContext.load()
        except Exception as exc:
            logger.critical("Fatal: cannot load IAgentContext: %s", exc)
            sys.exit(1)

        try:
            asyncio.run(self._serve(context))
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
