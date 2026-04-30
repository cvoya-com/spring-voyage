"""
Wire-shape tests for the A2A v0.3 sidecar (#1368).

Pins the JSON structure emitted by sidecar.py against the contract the .NET
A2A.V0_3 SDK expects. The authoritative reference is:
  deployment/agent-sidecar/src/a2a.ts  (TypeScript bridge that shipped in #1369)
  tests/Cvoya.Spring.Dapr.Tests/Execution/Fixtures/bridge-message-send-*.json

These tests DO NOT start an aiohttp server or an agent process.  They test the
_build_task_response helper and the JSON-RPC envelope shape directly.
"""
from __future__ import annotations

import json
import sys
import os

import pytest
from aiohttp.test_utils import TestClient, TestServer

# Add the parent directory to sys.path so we can import sidecar
sys.path.insert(0, os.path.join(os.path.dirname(__file__), ".."))

import sidecar


# ---------------------------------------------------------------------------
# _build_task_response unit tests
# ---------------------------------------------------------------------------

class TestBuildTaskResponse:
    """Pins the wire shape of _build_task_response against the A2A v0.3 spec."""

    def test_completed_task_has_kind_task(self):
        resp = sidecar._build_task_response("tid-1", "completed", "hello", None)
        assert resp["kind"] == "task", "top-level kind must be 'task'"

    def test_state_is_kebab_case_lower(self):
        for state in ("completed", "failed", "canceled", "working"):
            resp = sidecar._build_task_response("tid-1", state, None, None)
            assert resp["status"]["state"] == state

    def test_no_proto_prefix_in_state(self):
        """State values must NOT have the TASK_STATE_ prefix."""
        resp = sidecar._build_task_response("tid-1", "completed", "out", None)
        assert "TASK_STATE_" not in resp["status"]["state"]

    def test_completed_task_artifact_part_has_kind_text(self):
        resp = sidecar._build_task_response("tid-1", "completed", "hello world", None)
        parts = resp["artifacts"][0]["parts"]
        assert parts[0]["kind"] == "text"
        assert parts[0]["text"] == "hello world"

    def test_no_artifact_when_output_empty(self):
        resp = sidecar._build_task_response("tid-1", "completed", None, None)
        assert "artifacts" not in resp

    def test_failed_task_status_message_has_kind_message(self):
        resp = sidecar._build_task_response("tid-1", "failed", None, "boom")
        msg = resp["status"]["message"]
        assert msg["kind"] == "message"

    def test_failed_task_status_message_role_is_agent(self):
        resp = sidecar._build_task_response("tid-1", "failed", None, "boom")
        msg = resp["status"]["message"]
        assert msg["role"] == "agent"
        # Must NOT be the old proto-style ROLE_AGENT value
        assert msg["role"] != "ROLE_AGENT"

    def test_failed_task_status_message_has_message_id(self):
        resp = sidecar._build_task_response("tid-1", "failed", None, "boom")
        msg = resp["status"]["message"]
        assert "messageId" in msg and msg["messageId"]

    def test_failed_task_status_message_parts_have_kind_text(self):
        resp = sidecar._build_task_response("tid-1", "failed", None, "boom")
        parts = resp["status"]["message"]["parts"]
        assert parts[0]["kind"] == "text"
        assert parts[0]["text"] == "boom"

    def test_context_id_mirrors_task_id(self):
        resp = sidecar._build_task_response("my-task", "completed", None, None)
        assert resp["contextId"] == "my-task"

    def test_canceled_task_no_artifacts_no_message(self):
        resp = sidecar._build_task_response("tid-1", "canceled", None, None)
        assert resp["kind"] == "task"
        assert resp["status"]["state"] == "canceled"
        assert "artifacts" not in resp
        assert "message" not in resp["status"]


# ---------------------------------------------------------------------------
# Integration tests against the aiohttp app
# ---------------------------------------------------------------------------

@pytest.fixture
def app():
    return sidecar.create_app()


@pytest.mark.asyncio
async def test_message_send_result_is_flat_agent_task(aiohttp_client, app, monkeypatch):
    """message/send result must be the flat AgentTask, NOT wrapped under result.task.

    This is the key v0.3 invariant: the .NET SDK's SendMessageAsync reads
    result as A2AResponse using the kind discriminator at the top of result.
    """
    # Patch the global AGENT_CMD / AGENT_ARGS so the sidecar launches 'echo'
    monkeypatch.setattr(sidecar, "AGENT_CMD", "echo")
    monkeypatch.setattr(sidecar, "AGENT_ARGS", ["hello"])

    client = await aiohttp_client(app)
    payload = {
        "jsonrpc": "2.0",
        "id": "1",
        "method": "message/send",
        "params": {
            "message": {
                "parts": [{"text": "ping"}]
            }
        },
    }
    resp = await client.post("/a2a", json=payload)
    assert resp.status == 200
    body = await resp.json()

    # result must be the AgentTask directly, not {"task": <AgentTask>}
    result = body["result"]
    assert "task" not in result, "result must NOT be wrapped under result.task (v0.3 change)"
    assert result["kind"] == "task"
    assert result["status"]["state"] == "completed"


@pytest.mark.asyncio
async def test_message_send_completed_artifact_parts_have_kind(aiohttp_client, app, monkeypatch):
    """Artifact parts must carry kind: 'text'."""
    monkeypatch.setattr(sidecar, "AGENT_CMD", "echo")
    monkeypatch.setattr(sidecar, "AGENT_ARGS", ["-n", "output-text"])

    client = await aiohttp_client(app)
    payload = {
        "jsonrpc": "2.0",
        "id": "2",
        "method": "message/send",
        "params": {"message": {"parts": [{"text": "go"}]}},
    }
    resp = await client.post("/a2a", json=payload)
    body = await resp.json()
    result = body["result"]

    if result.get("artifacts"):
        for artifact in result["artifacts"]:
            for part in artifact["parts"]:
                assert part["kind"] == "text"


@pytest.mark.asyncio
async def test_tasks_get_result_is_flat_agent_task_with_kind(aiohttp_client, app, monkeypatch):
    """tasks/get result must be the flat AgentTask with kind: 'task'."""
    monkeypatch.setattr(sidecar, "AGENT_CMD", "echo")
    monkeypatch.setattr(sidecar, "AGENT_ARGS", ["hi"])

    client = await aiohttp_client(app)

    # First send a message to create a task
    send_payload = {
        "jsonrpc": "2.0", "id": "3", "method": "message/send",
        "params": {"message": {"parts": [{"text": "go"}]}},
    }
    send_resp = await client.post("/a2a", json=send_payload)
    send_body = await send_resp.json()
    task_id = send_body["result"]["id"]

    # Now get the task
    get_payload = {
        "jsonrpc": "2.0", "id": "4", "method": "tasks/get",
        "params": {"id": task_id},
    }
    get_resp = await client.post("/a2a", json=get_payload)
    get_body = await get_resp.json()
    result = get_body["result"]

    assert result["kind"] == "task"
    assert result["id"] == task_id
    # State must be kebab-case-lower
    assert result["status"]["state"] in ("completed", "failed", "canceled", "working")
    assert "TASK_STATE_" not in result["status"]["state"]


@pytest.mark.asyncio
async def test_tasks_cancel_result_has_kind_task(aiohttp_client, app, monkeypatch):
    """tasks/cancel result must be AgentTask with kind: 'task'."""
    monkeypatch.setattr(sidecar, "AGENT_CMD", "echo")
    monkeypatch.setattr(sidecar, "AGENT_ARGS", ["hi"])

    client = await aiohttp_client(app)

    # Create a task
    send_payload = {
        "jsonrpc": "2.0", "id": "5", "method": "message/send",
        "params": {"message": {"parts": [{"text": "go"}]}},
    }
    send_resp = await client.post("/a2a", json=send_payload)
    send_body = await send_resp.json()
    task_id = send_body["result"]["id"]

    # Cancel it (even if already completed, the cancel response shape must be correct)
    cancel_payload = {
        "jsonrpc": "2.0", "id": "6", "method": "tasks/cancel",
        "params": {"id": task_id},
    }
    cancel_resp = await client.post("/a2a", json=cancel_payload)
    cancel_body = await cancel_resp.json()
    result = cancel_body["result"]

    assert result["kind"] == "task"
    assert result["id"] == task_id
    assert "TASK_STATE_" not in result["status"]["state"]
