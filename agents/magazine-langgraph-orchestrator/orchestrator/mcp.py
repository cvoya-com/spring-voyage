"""
Per-call MCP client for the orchestrator (ADR-0066 §2).

The orchestrator reaches the platform's `sv.*` tools through the single MCP
server (`$SPRING_MCP_URL`). Crucially it authenticates each call with the
**per-turn** token delivered on the inbound message (`Message.mcp_token`), not
a token cached at initialize() — for an always-on process that token is empty
at cold-start and revoked after the first turn.

Only the handful of tools the orchestrator actually drives are wrapped here:
`sv.directory.get_self` / `sv.directory.list_members` to resolve a peer role to
an address, and `sv.messaging.send` / `sv.messaging.respond_to` to deliver
briefs and finished work. Tool names and argument shapes match
`SvMessagingSkillRegistry` / `SvDirectorySkillRegistry`.

`httpx` is the only third-party import; an injectable transport keeps the
client unit-testable without a live MCP server.
"""

from __future__ import annotations

import json
from typing import Any

import httpx

SEND_TOOL = "sv.messaging.send"
RESPOND_TO_TOOL = "sv.messaging.respond_to"
GET_SELF_TOOL = "sv.directory.get_self"
LIST_MEMBERS_TOOL = "sv.directory.list_members"


class McpError(RuntimeError):
    """An MCP tool call returned a JSON-RPC error."""


class McpClient:
    """Thin JSON-RPC client over the platform MCP server."""

    def __init__(
        self,
        endpoint: str,
        *,
        timeout: float = 120.0,
        transport: httpx.AsyncBaseTransport | None = None,
    ) -> None:
        self._endpoint = endpoint
        self._timeout = timeout
        self._transport = transport

    async def call_tool(self, token: str, name: str, arguments: dict[str, Any]) -> str:
        """Invoke an MCP tool; return the joined text content of the result."""
        payload = {
            "jsonrpc": "2.0",
            "id": 1,
            "method": "tools/call",
            "params": {"name": name, "arguments": arguments},
        }
        headers = {
            "Content-Type": "application/json",
            "Authorization": f"Bearer {token}",
        }
        async with httpx.AsyncClient(
            timeout=self._timeout, transport=self._transport
        ) as client:
            resp = await client.post(self._endpoint, json=payload, headers=headers)
            resp.raise_for_status()
        body = resp.json()
        if body.get("error"):
            raise McpError(f"{name} failed: {json.dumps(body['error'])}")
        result = body.get("result", {})
        if result.get("isError"):
            raise McpError(f"{name} returned isError: {json.dumps(result)}")
        content = result.get("content", [])
        texts = [c.get("text", "") for c in content if c.get("type") == "text"]
        return "\n".join(texts) if texts else json.dumps(result)

    async def call_tool_json(
        self, token: str, name: str, arguments: dict[str, Any]
    ) -> Any:
        """Like :meth:`call_tool` but parse the text result as JSON when possible."""
        text = await self.call_tool(token, name, arguments)
        try:
            return json.loads(text)
        except (ValueError, TypeError):
            return text


# --- typed tool wrappers --------------------------------------------------


async def get_self(mcp: McpClient, token: str) -> dict[str, Any]:
    result = await mcp.call_tool_json(token, GET_SELF_TOOL, {})
    return result if isinstance(result, dict) else {}


async def list_members(
    mcp: McpClient, token: str, unit_uuid: str
) -> list[dict[str, Any]]:
    """List a unit's members. Returns directory entries (each with `address`
    and `roles`). The result shape may be a bare array or wrapped in a
    `members` key — both are handled."""
    result = await mcp.call_tool_json(token, LIST_MEMBERS_TOOL, {"uuid": unit_uuid})
    if isinstance(result, list):
        return [m for m in result if isinstance(m, dict)]
    if isinstance(result, dict):
        members = result.get("members") or result.get("entries") or []
        return [m for m in members if isinstance(m, dict)]
    return []


async def send_message(
    mcp: McpClient,
    token: str,
    recipients: list[str],
    message: str,
    reason: str | None = None,
) -> str:
    """`sv.messaging.send` — one-way delivery to one shared conversation."""
    args: dict[str, Any] = {"recipients": recipients, "message": message}
    if reason:
        args["reason"] = reason
    return await mcp.call_tool(token, SEND_TOOL, args)


async def respond_to(
    mcp: McpClient,
    token: str,
    message_id: str,
    message: str,
    reason: str | None = None,
) -> str:
    """`sv.messaging.respond_to` — continue the conversation a message belongs to."""
    args: dict[str, Any] = {"message_id": message_id, "message": message}
    if reason:
        args["reason"] = reason
    return await mcp.call_tool(token, RESPOND_TO_TOOL, args)


def resolve_role_address(members: list[dict[str, Any]], role: str) -> str | None:
    """Find the sendable address of the member holding *role*.

    Directory entries carry `roles` (a list) and a top-level `address`. Returns
    the first matching member's address, or ``None`` when no member holds the
    role.
    """
    for member in members:
        roles = member.get("roles") or []
        if isinstance(roles, list) and role in roles:
            address = member.get("address")
            if isinstance(address, str) and address:
                return address
    return None
