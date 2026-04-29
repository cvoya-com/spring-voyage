"""
CLI entry point for the Spring Voyage Agent SDK.

Usage
-----
Run a module that exposes the three lifecycle hooks::

    spring-voyage-agent --module my_agent

where ``my_agent.py`` (or ``my_agent/__init__.py``) defines:

    async def initialize(context: IAgentContext) -> None: ...
    async def on_message(message: Message) -> AsyncIterator[Response]: ...
    async def on_shutdown(reason: ShutdownReason) -> None: ...

The three callables are discovered by name from the module's namespace.
"""

from __future__ import annotations

import argparse
import importlib
import logging
import sys

logger = logging.getLogger("spring-voyage-agent.cli")


def main() -> None:
    parser = argparse.ArgumentParser(
        prog="spring-voyage-agent",
        description=(
            "Spring Voyage Agent SDK runtime. "
            "Loads a Python module that implements the three lifecycle hooks "
            "(initialize, on_message, on_shutdown) and runs the A2A server."
        ),
    )
    parser.add_argument(
        "--module",
        required=True,
        metavar="MODULE",
        help=(
            "Python module path (importable) that defines the three lifecycle "
            "hooks: initialize, on_message, on_shutdown. Example: my_agent"
        ),
    )
    parser.add_argument(
        "--port",
        type=int,
        default=None,
        metavar="PORT",
        help="A2A server listen port (default: AGENT_PORT env var or 8999).",
    )
    args = parser.parse_args()

    try:
        module = importlib.import_module(args.module)
    except ModuleNotFoundError as exc:
        print(f"error: cannot import module {args.module!r}: {exc}", file=sys.stderr)
        sys.exit(1)

    missing = [name for name in ("initialize", "on_message", "on_shutdown") if not hasattr(module, name)]
    if missing:
        print(
            f"error: module {args.module!r} is missing required hooks: {', '.join(missing)}",
            file=sys.stderr,
        )
        sys.exit(1)

    from spring_voyage_agent.runtime import run

    run(
        initialize=getattr(module, "initialize"),
        on_message=getattr(module, "on_message"),
        on_shutdown=getattr(module, "on_shutdown"),
        port=args.port,
    )


if __name__ == "__main__":
    main()
