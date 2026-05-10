"""Regression test for #2088: persistent-hosted unit agent must keep its
A2A / uvicorn server up across multiple ``message/send`` requests on the
same thread.

Pre-fix symptom: after the first message the runtime tore down the
uvicorn server (logged ``Application shutdown complete.\\nFinished server
process [N]``) and the next dispatch reached ``daprd`` but found no app
listening, surfacing as a 502.

Strategy: boot ``AgentRuntime`` in a background thread on an ephemeral
port, send two JSON-RPC ``message/send`` calls to it, and assert that
the second call still gets a response. The agent-card endpoint is
re-checked at the end as a redundant liveness probe.
"""

from __future__ import annotations

import asyncio
import socket
import threading
import time
import uuid
from typing import Any

import httpx

from spring_voyage_agent_sdk.hooks import AgentHooks
from spring_voyage_agent_sdk.runtime import AgentRuntime
from spring_voyage_agent_sdk.types import Response


def _free_port() -> int:
    with socket.socket(socket.AF_INET, socket.SOCK_STREAM) as s:
        s.bind(("127.0.0.1", 0))
        return s.getsockname()[1]


def _wait_for_card(port: int, timeout: float = 5.0) -> None:
    """Poll the agent-card endpoint until it returns 200 or the timeout trips."""
    deadline = time.monotonic() + timeout
    last_exc: Exception | None = None
    while time.monotonic() < deadline:
        try:
            r = httpx.get(f"http://127.0.0.1:{port}/.well-known/agent-card.json", timeout=0.5)
            if r.status_code == 200:
                return
        except Exception as exc:  # noqa: BLE001 — startup race, expected
            last_exc = exc
        time.sleep(0.05)
    raise AssertionError(f"agent-card did not become ready within {timeout}s; last error: {last_exc!r}")


def _send_message(port: int, *, text: str, context_id: str, message_id: str) -> dict[str, Any]:
    """Send a JSON-RPC ``message/send`` request and return the parsed JSON body.

    Mirrors the wire shape produced by the .NET ``A2AClient`` (A2A v0.3),
    which is what Spring Voyage's dispatcher uses.
    """
    payload = {
        "jsonrpc": "2.0",
        "id": message_id,
        "method": "message/send",
        "params": {
            "message": {
                "role": "user",
                "parts": [{"kind": "text", "text": text}],
                "messageId": message_id,
                "contextId": context_id,
            },
            "configuration": {
                "acceptedOutputModes": ["text/plain"],
            },
        },
    }
    r = httpx.post(f"http://127.0.0.1:{port}/", json=payload, timeout=5.0)
    r.raise_for_status()
    return r.json()


def test_runtime_serves_multiple_messages_on_same_thread() -> None:
    """Acceptance criterion #1 + #2 of #2088, exercised at the SDK level.

    Two A2A ``message/send`` calls on the same ``contextId`` must both
    succeed; the agent-card endpoint must still be reachable after both.
    Pre-fix, the runtime tore the uvicorn server down after the first
    request, so the second call (or the post-request agent-card probe)
    failed with a connection error.
    """
    port = _free_port()

    invocations: list[str] = []

    async def _initialize(_ctx) -> None:  # IAgentContext.load() will fail (no env vars), so this is never called.
        pass

    async def _on_message(message):  # async generator; SDK collects yielded chunks.
        invocations.append(message.text or "")
        yield Response(text=f"echo:{message.text or ''}", final=True)

    async def _on_shutdown(_reason) -> None:
        pass

    hooks = AgentHooks(
        initialize=_initialize,
        on_message=_on_message,
        on_shutdown=_on_shutdown,
    )

    runtime = AgentRuntime(hooks, port=port)
    # Bypass the IAgentContext bootstrap (no env vars in the test harness):
    # set _initialize_done directly so the executor will dispatch on_message.
    runtime._initialize_done.set()

    # Run the asyncio event loop in a background thread so we can drive
    # the server with synchronous httpx calls from the test thread.
    started = threading.Event()
    error: list[BaseException] = []

    def _runner() -> None:
        try:
            started.set()
            asyncio.run(runtime._serve())
        except BaseException as exc:  # noqa: BLE001 — propagate any failure to the test thread
            error.append(exc)

    thread = threading.Thread(target=_runner, name="agent-runtime", daemon=True)
    thread.start()
    started.wait(timeout=2.0)

    try:
        _wait_for_card(port)

        thread_id = str(uuid.uuid4())

        first = _send_message(port, text="hello-1", context_id=thread_id, message_id=str(uuid.uuid4()))
        assert "result" in first, f"first message returned an error: {first!r}"

        # The bug: this second call used to fail because uvicorn had shut down.
        second = _send_message(port, text="hello-2", context_id=thread_id, message_id=str(uuid.uuid4()))
        assert "result" in second, f"second message returned an error: {second!r}"

        # Liveness re-probe: the agent card must still answer 200.
        card = httpx.get(f"http://127.0.0.1:{port}/.well-known/agent-card.json", timeout=1.0)
        assert card.status_code == 200, (
            f"agent-card stopped responding after two message/send calls — uvicorn likely shut down. "
            f"Status={card.status_code} body={card.text!r}"
        )

        # Both messages must have been delivered to the on_message hook.
        assert invocations == ["hello-1", "hello-2"], (
            f"on_message hook saw {invocations!r}, expected both messages on the same thread"
        )
    finally:
        # Trip the SDK shutdown event so _serve() returns and the thread exits.
        # Calling on the runtime's own loop requires call_soon_threadsafe but the
        # event was created on the runner thread's loop; the simplest cross-thread
        # trip is to mark force_exit + cancel via uvicorn's flag. Easiest of all:
        # let the daemon thread die when the test process exits.
        pass

    if error:
        raise error[0]


def test_uvicorn_signal_capture_is_suppressed() -> None:
    """Issue #2088 root cause: uvicorn's ``Server.serve`` reinstalls SIGTERM
    via ``signal.signal()`` and silently overrides whatever handler the SDK
    put there with ``loop.add_signal_handler``.  Once that happens any
    SIGTERM the agent container receives — from the orchestrator, the
    sidecar, or a transient runtime hiccup — flips uvicorn's ``should_exit``
    directly, the A2A server tears down, and the SDK's
    ``on_shutdown(reason)`` hook is never called.

    The SDK ships a uvicorn subclass whose ``capture_signals`` is a no-op
    so the SDK retains sole authority over signal handling.  This test
    pins the contract.
    """
    import uvicorn

    from spring_voyage_agent_sdk.runtime import _SdkUvicornServer

    # Subclass MUST inherit from the real uvicorn.Server and MUST override
    # capture_signals to a no-op.  We don't need to actually run a server
    # here — verifying the contract at the class level is enough.
    assert issubclass(_SdkUvicornServer, uvicorn.Server)

    # The subclass must override capture_signals from the base class.
    base_method = uvicorn.Server.capture_signals
    sub_method = _SdkUvicornServer.capture_signals
    assert sub_method is not base_method, (
        "_SdkUvicornServer.capture_signals must override uvicorn.Server.capture_signals "
        "to keep uvicorn from reinstalling SIGTERM/SIGINT handlers (issue #2088)."
    )

    # Sanity: the override is a context manager that yields immediately
    # (rather than e.g. installing handlers itself).  Run the manager and
    # confirm it accepts the same protocol.
    server = _SdkUvicornServer(uvicorn.Config(app=lambda *_: None, log_config=None))
    cm = server.capture_signals()
    # Must support context-manager protocol.
    assert hasattr(cm, "__enter__") and hasattr(cm, "__exit__"), (
        "capture_signals override must remain a context manager so "
        "uvicorn.Server.serve's `with self.capture_signals(): ...` still works."
    )
    with cm:
        # Inside the override: no signal handlers should have been installed.
        # (We can't easily check "no signal change" without mutating signal
        # state from the test thread; the structural check above is the
        # load-bearing assertion.)
        pass

    # Bonus: the override must not actually invoke the signal module at all
    # — auditing the function bytecode catches accidental regressions
    # (e.g. someone re-adding signal.signal calls without updating the
    # docstring).  Bytecode-level scan is comment-agnostic, unlike a source
    # grep, so it's safe even when the docstring discusses signals.
    code = (
        _SdkUvicornServer.capture_signals.__wrapped__.__code__
        if hasattr(_SdkUvicornServer.capture_signals, "__wrapped__")
        else _SdkUvicornServer.capture_signals.__code__
    )
    assert "signal" not in code.co_names, (
        "_SdkUvicornServer.capture_signals must not call into the signal module — "
        "the whole point is to suppress uvicorn's signal-capture (issue #2088). "
        f"Found signal references in co_names: {code.co_names!r}"
    )
