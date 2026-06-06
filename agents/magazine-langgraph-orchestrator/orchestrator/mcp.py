"""
Per-call MCP client for the orchestrator (ADR-0066).

The orchestrator reaches the platform's `sv.*` tools through the single MCP
server (`$SPRING_MCP_URL`). It authenticates each call with the engine's
**durable, agent-scoped** token (ADR-0066 §2) — a service identity valid for
the container's lifetime, so calls work whether or not the engine is processing
an inbound message. The caller supplies the token per call.

Only the handful of tools the orchestrator drives are wrapped here:
`sv.directory.get_self` / `sv.directory.list_members` to resolve a peer role to
an address, and `sv.messaging.send` / `sv.messaging.respond_to` to deliver
briefs and finished work. `send` / `respond_to` return the created message's id
so the orchestrator can correlate a later reply by its `in_reply_to`
(ADR-0066 §5). Tool names and argument shapes match `SvMessagingSkillRegistry` /
`SvDirectorySkillRegistry`.

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
    """List a unit's members. Returns directory entries, each carrying `roles`
    (a list) and a sendable `address`. The directory wire shape exposes the
    address as separate `kind` + `uuid` fields rather than a pre-built string,
    so we materialise the canonical `kind:uuid` address on each entry — role and
    address resolution (and `resolve_role_address`) thread by it. The result
    shape may be a bare array or wrapped in a `members` key — both are handled."""
    result = await mcp.call_tool_json(token, LIST_MEMBERS_TOOL, {"uuid": unit_uuid})
    if isinstance(result, list):
        raw = result
    elif isinstance(result, dict):
        raw = result.get("members") or result.get("entries") or []
    else:
        raw = []
    members = [m for m in raw if isinstance(m, dict)]
    for member in members:
        _ensure_member_address(member)
    return members


def _ensure_member_address(member: dict[str, Any]) -> None:
    """Materialise a member's sendable `address` from the directory's `kind` +
    `uuid` fields when the entry has no pre-built `address`. The canonical Spring
    Voyage address is `kind:uuid` (no-dash 32-char hex), e.g.
    `agent:e194bc95b7c748c3a3fc797dba6b598d`."""
    if member.get("address"):
        return
    kind = member.get("kind")
    uuid = member.get("uuid")
    if isinstance(kind, str) and kind and isinstance(uuid, str) and uuid:
        member["address"] = f"{kind}:{uuid}"


def _created_message_id(ack: Any) -> str | None:
    """Pull the created message's id out of a send/respond_to ack.

    The ack is ``{"messageId": "...", "deliveries": [...]}`` (ADR-0049). The id
    lets the orchestrator correlate a later reply by its ``in_reply_to``."""
    if isinstance(ack, dict):
        mid = ack.get("messageId") or ack.get("message_id")
        return str(mid) if mid else None
    return None


async def send_message(
    mcp: McpClient,
    token: str,
    recipients: list[str],
    message: str,
    reason: str | None = None,
) -> str | None:
    """`sv.messaging.send` — one-way delivery to one shared conversation.

    Returns the created message's id (for correlation), or ``None``."""
    args: dict[str, Any] = {"recipients": recipients, "message": message}
    if reason:
        args["reason"] = reason
    return _created_message_id(await mcp.call_tool_json(token, SEND_TOOL, args))


async def respond_to(
    mcp: McpClient,
    token: str,
    message_id: str,
    message: str,
    reason: str | None = None,
) -> str | None:
    """`sv.messaging.respond_to` — continue the conversation a message belongs to.

    Returns the created reply's id, or ``None``."""
    args: dict[str, Any] = {"message_id": message_id, "message": message}
    if reason:
        args["reason"] = reason
    return _created_message_id(await mcp.call_tool_json(token, RESPOND_TO_TOOL, args))


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
