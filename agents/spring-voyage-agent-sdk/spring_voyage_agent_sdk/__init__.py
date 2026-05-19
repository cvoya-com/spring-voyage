"""
Spring Voyage Agent SDK — Python lifecycle-hooks package.

Implements the Bucket 1 contract from the agent-runtime-boundary spec
(docs/specs/agent-runtime-boundary.md §1):

    initialize(context: IAgentContext) -> None
    on_message(message: Message) -> Iterable[Response] | AsyncIterable[Response]
    on_shutdown(reason: ShutdownReason) -> None

Agent authors implement these three callables and hand them to the SDK
runtime via :func:`run`. The SDK handles the A2A server, SIGTERM
trapping, lifecycle ordering, and concurrent-thread scheduling. The agent
authors write only the business logic.

Quickstart
----------
>>> import asyncio
>>> from spring_voyage_agent_sdk import (
...     IAgentContext,
...     Message,
...     Response,
...     ShutdownReason,
...     RuntimeContext,
...     run,
... )
>>>
>>> async def initialize(context: IAgentContext) -> None:
...     print(f"Starting agent {context.agent_id}")
>>>
>>> async def on_message(message: Message):
...     ctx = RuntimeContext.current()
...     ctx.report_progress("starting work")
...     async with ctx.tool_call("acme.echo", args={"text": message.text}) as tc:
...         tc.set_result(message.text)
...     yield Response(text=f"Echo: {message.text}", final=True)
>>>
>>> async def on_shutdown(reason: ShutdownReason) -> None:
...     print(f"Shutting down: {reason.value}")
>>>
>>> if __name__ == "__main__":
...     run(initialize=initialize, on_message=on_message, on_shutdown=on_shutdown)

Telemetry primitives (issue #2493)
----------------------------------

The SDK exposes a per-turn :class:`RuntimeContext` with three emit
primitives backed by OTLP/HTTP+JSON to the platform's
``/otlp/v1/{traces,logs}`` ingest (introduced in #2492):

  * ``RuntimeContext.report_progress(text, kind=None, attrs=None)``
  * ``async with RuntimeContext.tool_call(name, args) as tc``
  * ``async with RuntimeContext.llm_turn(model, prompt) as t``

A token-bucket :class:`ProgressRateLimiter` bounds the per-(subject,
kind) emission rate; defaults are 5/s sustained with a burst of 20,
env-overridable via ``SV_PROGRESS_RATE_LIMIT_RPS`` /
``SV_PROGRESS_RATE_LIMIT_BURST``.

The SDK runtime enforces **response discipline** as a safety net: a
turn that exits without yielding a :class:`Response(final=True)`
triggers a synthesized final reply, a
``response_discipline_violation`` telemetry event, and a stderr
warning — the platform user always sees *something* even when an
agent author forgets to reply.
"""

from spring_voyage_agent_sdk.context import IAgentContext
from spring_voyage_agent_sdk.hooks import AgentHooks
from spring_voyage_agent_sdk.rate_limit import (
    ProgressRateLimiter,
    default_limiter,
    reset_default_limiter,
)
from spring_voyage_agent_sdk.runtime import run
from spring_voyage_agent_sdk.runtime_context import (
    KIND_LLM_TURN,
    KIND_PROGRESS,
    KIND_RESPONSE_DISCIPLINE_VIOLATION,
    KIND_TOOL_CALL,
    LlmTurnSpan,
    RuntimeContext,
    ToolCallSpan,
    llm_turn,
    report_progress,
    tool_call,
)
from spring_voyage_agent_sdk.telemetry import TelemetryEmitter
from spring_voyage_agent_sdk.types import Message, Response, ShutdownReason

__all__ = [
    "IAgentContext",
    "AgentHooks",
    "Message",
    "Response",
    "ShutdownReason",
    "RuntimeContext",
    "ToolCallSpan",
    "LlmTurnSpan",
    "TelemetryEmitter",
    "ProgressRateLimiter",
    "default_limiter",
    "reset_default_limiter",
    "KIND_PROGRESS",
    "KIND_TOOL_CALL",
    "KIND_LLM_TURN",
    "KIND_RESPONSE_DISCIPLINE_VIOLATION",
    "report_progress",
    "tool_call",
    "llm_turn",
    "run",
]
