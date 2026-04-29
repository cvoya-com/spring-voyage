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
>>> from spring_voyage_agent import IAgentContext, Message, Response, ShutdownReason, run
>>>
>>> async def initialize(context: IAgentContext) -> None:
...     print(f"Starting agent {context.agent_id}")
>>>
>>> async def on_message(message: Message):
...     yield Response(text=f"Echo: {message.text}")
>>>
>>> async def on_shutdown(reason: ShutdownReason) -> None:
...     print(f"Shutting down: {reason.value}")
>>>
>>> if __name__ == "__main__":
...     run(initialize=initialize, on_message=on_message, on_shutdown=on_shutdown)
"""

from spring_voyage_agent.context import IAgentContext
from spring_voyage_agent.hooks import AgentHooks
from spring_voyage_agent.runtime import run
from spring_voyage_agent.types import Message, Response, ShutdownReason

__all__ = [
    "IAgentContext",
    "AgentHooks",
    "Message",
    "Response",
    "ShutdownReason",
    "run",
]
