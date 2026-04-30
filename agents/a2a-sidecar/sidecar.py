#!/usr/bin/env python3
"""
A2A sidecar adapter for CLI-based agents.

Exposes an A2A-compliant HTTP endpoint that:
- Serves the Agent Card at /.well-known/agent.json
- Handles POST /a2a for SendMessage / CancelTask (JSON-RPC 2.0)
- Launches the agent CLI process on SendMessage, pipes stdin/stdout,
  and streams output as A2A TaskStatusUpdate / TaskArtifactUpdate events.
- Sends SIGTERM to the CLI process on CancelTask.

Configuration via environment variables:
- AGENT_CMD:        The CLI command to launch (default: "claude")
- AGENT_ARGS:       Space-separated arguments for the CLI (default: "")
- AGENT_NAME:       Display name for the Agent Card (default: "CLI Agent")
- AGENT_PORT:       Port to listen on (default: 8999)
- SPRING_SYSTEM_PROMPT: System prompt passed to the agent via stdin
- SPRING_MCP_ENDPOINT:  MCP server URL the agent should connect to
- SPRING_AGENT_TOKEN:   Bearer token for MCP authentication

Wire shape: A2A v0.3 per the .NET A2A.V0_3 SDK contract (issue #1198 / #1368).
  - Enum values are kebab-case-lower ("completed", "failed", "agent", ...)
  - Every AgentTask carries kind: "task" (A2AEventConverterViaKindDiscriminator)
  - Part objects carry kind: "text" (PartConverterViaKindDiscriminator)
  - Status messages carry kind: "message" (AgentMessage discriminator)
  - message/send result is the flat AgentTask - NOT wrapped under result.task
"""

import asyncio
import json
import logging
import os
import signal
import uuid
from datetime import datetime, timezone

from aiohttp import web

logging.basicConfig(level=logging.INFO, format="%(asctime)s %(levelname)s %(message)s")
logger = logging.getLogger("a2a-sidecar")

AGENT_CMD = os.environ.get("AGENT_CMD", "claude")
AGENT_ARGS = os.environ.get("AGENT_ARGS", "").split() if os.environ.get("AGENT_ARGS") else []
AGENT_NAME = os.environ.get("AGENT_NAME", "CLI Agent")
AGENT_PORT = int(os.environ.get("AGENT_PORT", "8999"))

# Active tasks: task_id -> { "process": Process, "state": str, "output": str,
#                            "error_message": str | None }
active_tasks: dict[str, dict] = {}

# A2A v0.3 TaskState values - kebab-case-lower per
# KebabCaseLowerJsonStringEnumConverter on the .NET side (#1198).
_STATE_WORKING = "working"
_STATE_COMPLETED = "completed"
_STATE_FAILED = "failed"
_STATE_CANCELED = "canceled"


def _build_task_response(
    task_id: str,
    state: str,
    output: "str | None",
    error_message: "str | None",
) -> dict:
    """Build a v0.3-conformant AgentTask payload.

    Wire shape per A2A v0.3 and the .NET A2A.V0_3 SDK:
    - kind: "task" at top level (A2AEventConverterViaKindDiscriminator + [JsonRequired])
    - status.state is kebab-case-lower (KebabCaseLowerJsonStringEnumConverter)
    - status.message carries kind: "message" (AgentMessage discriminator)
    - parts carry kind: "text" (PartConverterViaKindDiscriminator)

    Mirrors deployment/agent-sidecar/src/a2a.ts buildTaskResponse (#1368).
    """
    response: dict = {
        # A2A v0.3 kind discriminator ([JsonRequired] on AgentTask).
        "kind": "task",
        "id": task_id,
        # contextId is [JsonRequired] on A2A.V0_3.AgentTask; the sidecar
        # mirrors the per-task id since it has no separate conversation handle.
        "contextId": task_id,
        "status": {
            "state": state,
            "timestamp": datetime.now(timezone.utc).isoformat(),
        },
    }

    artifacts: list[dict] = []
    if output:
        artifacts.append({
            "artifactId": str(uuid.uuid4()),
            # A2A v0.3: Part objects carry a "kind" discriminator.
            "parts": [{"kind": "text", "text": output}],
        })
    if artifacts:
        response["artifacts"] = artifacts

    if error_message is not None:
        # AgentMessage embedded in TaskStatus.Message is also polymorphic in
        # the V0_3 SDK and serialized with kind: "message". role and messageId
        # are [JsonRequired]; we mint a fresh messageId per error message.
        response["status"]["message"] = {
            "kind": "message",
            "role": "agent",
            "messageId": str(uuid.uuid4()),
            "parts": [{"kind": "text", "text": error_message}],
        }

    return response


def build_agent_card() -> dict:
    """Build a minimal A2A Agent Card."""
    return {
        "name": AGENT_NAME,
        "description": f"A2A sidecar wrapping '{AGENT_CMD}' CLI agent",
        "provider": {"organization": "Spring Voyage"},
        "version": "1.0.0",
        "capabilities": {
            "streaming": False,
            "pushNotifications": False,
        },
        "skills": [
            {
                "id": "execute",
                "name": "Execute Task",
                "description": "Sends a task to the wrapped CLI agent for execution.",
            }
        ],
        "interfaces": [
            {
                "protocol": "jsonrpc/http",
                "url": f"http://localhost:{AGENT_PORT}/a2a",
            }
        ],
    }


async def handle_agent_card(request: web.Request) -> web.Response:
    """Serve the Agent Card at /.well-known/agent.json."""
    return web.json_response(build_agent_card())


async def handle_a2a(request: web.Request) -> web.Response:
    """Handle JSON-RPC 2.0 A2A requests."""
    try:
        body = await request.json()
    except json.JSONDecodeError:
        return web.json_response(
            {"jsonrpc": "2.0", "error": {"code": -32700, "message": "Parse error"}, "id": None},
            status=400,
        )

    method = body.get("method")
    params = body.get("params", {})
    rpc_id = body.get("id")

    if method == "message/send":
        return await handle_send_message(params, rpc_id)
    elif method == "tasks/cancel":
        return await handle_cancel_task(params, rpc_id)
    elif method == "tasks/get":
        return await handle_get_task(params, rpc_id)
    else:
        return web.json_response(
            {
                "jsonrpc": "2.0",
                "error": {"code": -32601, "message": f"Method not found: {method}"},
                "id": rpc_id,
            },
            status=400,
        )


async def handle_send_message(params: dict, rpc_id) -> web.Response:
    """Launch the CLI agent and return the result as an A2A v0.3 task.

    A2A v0.3: message/send result is the flat AgentTask (kind: "task") consumed
    by the .NET SDK's SendMessageAsync as A2AResponse via the kind discriminator.
    No "task" wrapper - the AgentTask itself is the result, identified by its
    top-level "kind" field. (#1198 / #1368)
    """
    message = params.get("message", {})
    parts = message.get("parts", [])
    user_text = ""
    for part in parts:
        if "text" in part:
            user_text += part["text"]

    task_id = str(uuid.uuid4())

    active_tasks[task_id] = {
        "process": None,
        "state": _STATE_WORKING,
        "output": "",
        "error_message": None,
    }

    try:
        cmd = [AGENT_CMD] + AGENT_ARGS
        logger.info("Launching agent: %s (task %s)", " ".join(cmd), task_id)

        env = {**os.environ}

        process = await asyncio.create_subprocess_exec(
            *cmd,
            stdin=asyncio.subprocess.PIPE,
            stdout=asyncio.subprocess.PIPE,
            stderr=asyncio.subprocess.PIPE,
            env=env,
        )
        active_tasks[task_id]["process"] = process

        # Send the user text to stdin
        if process.stdin:
            process.stdin.write(user_text.encode())
            process.stdin.close()

        stdout_data, stderr_data = await process.communicate()

        exit_code = process.returncode or 0
        output = stdout_data.decode(errors="replace") if stdout_data else ""
        error_output = stderr_data.decode(errors="replace") if stderr_data else ""

        if exit_code == 0:
            active_tasks[task_id]["state"] = _STATE_COMPLETED
            active_tasks[task_id]["output"] = output
            active_tasks[task_id]["error_message"] = None

            task_response = _build_task_response(task_id, _STATE_COMPLETED, output, None)
        else:
            error_message = error_output or output or f"Agent CLI exited with code {exit_code}"
            active_tasks[task_id]["state"] = _STATE_FAILED
            active_tasks[task_id]["output"] = output
            active_tasks[task_id]["error_message"] = error_message

            task_response = _build_task_response(task_id, _STATE_FAILED, output or None, error_message)

        # A2A v0.3: message/send result is the flat AgentTask - NOT wrapped
        # under result.task. The .NET SDK's SendMessageAsync reads result as
        # A2AResponse using the kind discriminator at the top of the result.
        return web.json_response(
            {"jsonrpc": "2.0", "result": task_response, "id": rpc_id}
        )

    except Exception as exc:
        logger.exception("Agent execution failed for task %s", task_id)
        error_message = str(exc)
        active_tasks[task_id]["state"] = _STATE_FAILED
        active_tasks[task_id]["error_message"] = error_message

        task_response = _build_task_response(task_id, _STATE_FAILED, None, error_message)
        return web.json_response(
            {"jsonrpc": "2.0", "result": task_response, "id": rpc_id}
        )


async def handle_cancel_task(params: dict, rpc_id) -> web.Response:
    """Cancel a running task by sending SIGTERM to the CLI process.

    A2A v0.3: tasks/cancel result is the AgentTask with kind: "task".
    The .NET SDK's CancelTaskAsync deserializes the result as AgentTask
    directly; AgentTask is [JsonRequired] for "kind" so it must be present.
    """
    task_id = params.get("id")
    if not task_id or task_id not in active_tasks:
        return web.json_response(
            {
                "jsonrpc": "2.0",
                "error": {"code": -32001, "message": f"Task not found: {task_id}"},
                "id": rpc_id,
            },
            status=404,
        )

    entry = active_tasks[task_id]
    process = entry.get("process")
    if process and process.returncode is None:
        logger.info("Cancelling task %s (sending SIGTERM)", task_id)
        try:
            process.send_signal(signal.SIGTERM)
        except ProcessLookupError:
            pass

    entry["state"] = _STATE_CANCELED
    task_response = _build_task_response(task_id, _STATE_CANCELED, None, None)
    return web.json_response(
        {"jsonrpc": "2.0", "result": task_response, "id": rpc_id}
    )


async def handle_get_task(params: dict, rpc_id) -> web.Response:
    """Return current status of a task.

    A2A v0.3: tasks/get result is the AgentTask with kind: "task".
    The .NET SDK's GetTaskAsync deserializes the result as AgentTask
    directly; AgentTask is [JsonRequired] for "kind" so it must be present.
    """
    task_id = params.get("id")
    if not task_id or task_id not in active_tasks:
        return web.json_response(
            {
                "jsonrpc": "2.0",
                "error": {"code": -32001, "message": f"Task not found: {task_id}"},
                "id": rpc_id,
            },
            status=404,
        )

    entry = active_tasks[task_id]
    state = entry["state"]
    output = entry.get("output") or None
    error_message = entry.get("error_message")

    task_response = _build_task_response(task_id, state, output, error_message)
    return web.json_response(
        {"jsonrpc": "2.0", "result": task_response, "id": rpc_id}
    )


async def health_check(request: web.Request) -> web.Response:
    """Simple health-check endpoint for readiness probes."""
    return web.json_response({"status": "ok"})


def create_app() -> web.Application:
    """Create the aiohttp application with A2A routes."""
    app = web.Application()
    app.router.add_get("/.well-known/agent.json", handle_agent_card)
    app.router.add_post("/a2a", handle_a2a)
    app.router.add_get("/health", health_check)
    return app


if __name__ == "__main__":
    app = create_app()
    logger.info("Starting A2A sidecar on port %d (agent: %s)", AGENT_PORT, AGENT_CMD)
    web.run_app(app, host="0.0.0.0", port=AGENT_PORT)
