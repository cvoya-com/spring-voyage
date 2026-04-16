"""
MCP tool bridge for the Dapr Agent.

Discovers tools from a Spring Voyage MCP server and creates @tool-decorated
callables that proxy ``tools/call`` back to the platform.  The bridge lets the
Dapr Agent use every MCP tool the platform exposes without hard-coding tool
definitions — the set is resolved at startup via ``tools/list``.
"""

from __future__ import annotations

import json
import logging
from typing import Any

import httpx
from dapr_agents.tool import tool
from pydantic import BaseModel, create_model

logger = logging.getLogger("dapr-agent.mcp")

_JSON_RPC_HEADERS = {"Content-Type": "application/json"}


async def discover_tools(
    endpoint: str,
    token: str,
    *,
    timeout: float = 30.0,
) -> list[dict[str, Any]]:
    """Call MCP ``tools/list`` and return raw tool definitions."""
    payload = {
        "jsonrpc": "2.0",
        "id": 1,
        "method": "tools/list",
        "params": {},
    }
    headers = {**_JSON_RPC_HEADERS, "Authorization": f"Bearer {token}"}

    async with httpx.AsyncClient(timeout=timeout) as client:
        resp = await client.post(endpoint, json=payload, headers=headers)
        resp.raise_for_status()

    body = resp.json()
    if "error" in body:
        raise RuntimeError(f"MCP tools/list error: {body['error']}")

    tools_list: list[dict[str, Any]] = body.get("result", {}).get("tools", [])
    logger.info("Discovered %d MCP tools from %s", len(tools_list), endpoint)
    return tools_list


def _build_args_model(tool_def: dict[str, Any]) -> type[BaseModel] | None:
    """Build a Pydantic model from the MCP tool's ``inputSchema``."""
    schema = tool_def.get("inputSchema")
    if not schema or schema.get("type") != "object":
        return None

    properties: dict[str, Any] = schema.get("properties", {})
    required: set[str] = set(schema.get("required", []))

    field_definitions: dict[str, Any] = {}
    for name, prop in properties.items():
        py_type = str  # safe default
        json_type = prop.get("type", "string")
        if json_type == "integer":
            py_type = int
        elif json_type == "number":
            py_type = float
        elif json_type == "boolean":
            py_type = bool

        if name in required:
            field_definitions[name] = (py_type, ...)
        else:
            field_definitions[name] = (py_type | None, None)

    model_name = tool_def["name"].replace("-", "_").title().replace("_", "") + "Args"
    return create_model(model_name, **field_definitions)


def create_tool_proxy(
    tool_def: dict[str, Any],
    endpoint: str,
    token: str,
) -> Any:
    """Return a ``@tool``-decorated callable that proxies ``tools/call``."""
    tool_name: str = tool_def["name"]
    tool_description: str = tool_def.get("description", tool_name)
    args_model = _build_args_model(tool_def)

    async def _proxy(**kwargs: Any) -> str:
        payload = {
            "jsonrpc": "2.0",
            "id": 1,
            "method": "tools/call",
            "params": {"name": tool_name, "arguments": kwargs},
        }
        headers = {**_JSON_RPC_HEADERS, "Authorization": f"Bearer {token}"}

        async with httpx.AsyncClient(timeout=120.0) as client:
            resp = await client.post(endpoint, json=payload, headers=headers)
            resp.raise_for_status()

        body = resp.json()
        if "error" in body:
            return f"Error: {json.dumps(body['error'])}"

        result = body.get("result", {})
        # MCP tools/call returns { content: [ { type, text } ] }
        content = result.get("content", [])
        texts = [c.get("text", "") for c in content if c.get("type") == "text"]
        return "\n".join(texts) if texts else json.dumps(result)

    _proxy.__name__ = tool_name
    _proxy.__doc__ = tool_description

    if args_model:
        decorated = tool(args_model=args_model)(_proxy)
    else:
        decorated = tool(_proxy)

    return decorated
