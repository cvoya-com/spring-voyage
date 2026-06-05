"""Unit tests for the per-call MCP client (httpx MockTransport — no server)."""

from __future__ import annotations

import json

import httpx
import pytest

from orchestrator.mcp import (
    McpClient,
    McpError,
    list_members,
    resolve_role_address,
    send_message,
)


def _ok(result_text: str):
    def handler(request: httpx.Request) -> httpx.Response:
        return httpx.Response(
            200,
            json={
                "jsonrpc": "2.0",
                "id": 1,
                "result": {"content": [{"type": "text", "text": result_text}]},
            },
        )

    return httpx.MockTransport(handler)


@pytest.mark.asyncio
async def test_send_message_builds_jsonrpc_with_per_call_token():
    captured: dict = {}

    def handler(request: httpx.Request) -> httpx.Response:
        captured["auth"] = request.headers.get("authorization")
        captured["body"] = json.loads(request.content)
        return httpx.Response(
            200,
            json={
                "jsonrpc": "2.0",
                "id": 1,
                "result": {"content": [{"type": "text", "text": "delivered"}]},
            },
        )

    mcp = McpClient("http://mcp/", transport=httpx.MockTransport(handler))
    out = await send_message(mcp, "tok-turn-42", ["agent:abc"], "hello", reason="r")

    assert out == "delivered"
    assert captured["auth"] == "Bearer tok-turn-42"
    body = captured["body"]
    assert body["method"] == "tools/call"
    assert body["params"]["name"] == "sv.messaging.send"
    assert body["params"]["arguments"] == {
        "recipients": ["agent:abc"],
        "message": "hello",
        "reason": "r",
    }


@pytest.mark.asyncio
async def test_call_tool_raises_on_jsonrpc_error():
    def handler(request: httpx.Request) -> httpx.Response:
        return httpx.Response(
            200,
            json={
                "jsonrpc": "2.0",
                "id": 1,
                "error": {"code": -32000, "message": "boom"},
            },
        )

    mcp = McpClient("http://mcp/", transport=httpx.MockTransport(handler))
    with pytest.raises(McpError):
        await send_message(mcp, "t", ["a"], "m")


@pytest.mark.asyncio
async def test_list_members_parses_and_resolves_role():
    members_json = json.dumps([{"address": "agent:w", "roles": ["staff-writer"]}])
    mcp = McpClient("http://mcp/", transport=_ok(members_json))
    members = await list_members(mcp, "t", "unit-1")
    assert resolve_role_address(members, "staff-writer") == "agent:w"


def test_resolve_role_address_none_when_absent():
    assert resolve_role_address([{"address": "a", "roles": ["x"]}], "y") is None
