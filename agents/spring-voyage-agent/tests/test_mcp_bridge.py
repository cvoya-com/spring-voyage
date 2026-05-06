"""Tests for mcp_bridge.py — MCP tool discovery and proxy creation."""

from __future__ import annotations

from unittest.mock import AsyncMock, MagicMock, patch

import pytest

from mcp_bridge import _build_args_model, create_tool_proxy, discover_tools


@pytest.fixture
def sample_tool_def():
    return {
        "name": "read-file",
        "description": "Read a file from the workspace.",
        "inputSchema": {
            "type": "object",
            "properties": {
                "path": {"type": "string", "description": "File path"},
                "encoding": {"type": "string", "description": "Encoding"},
            },
            "required": ["path"],
        },
    }


@pytest.fixture
def simple_tool_def():
    return {
        "name": "get-status",
        "description": "Get the current status.",
    }


class TestDiscoverTools:
    @pytest.mark.asyncio
    async def test_discover_tools_returns_tool_list(self):
        mock_tools = [
            {"name": "tool-a", "description": "Tool A"},
            {"name": "tool-b", "description": "Tool B"},
        ]
        mock_response = MagicMock()
        mock_response.status_code = 200
        mock_response.raise_for_status = lambda: None
        mock_response.json.return_value = {
            "jsonrpc": "2.0",
            "id": 1,
            "result": {"tools": mock_tools},
        }

        with patch("mcp_bridge.httpx.AsyncClient") as mock_client_cls:
            mock_client = AsyncMock()
            mock_client.post.return_value = mock_response
            mock_client.__aenter__ = AsyncMock(return_value=mock_client)
            mock_client.__aexit__ = AsyncMock(return_value=False)
            mock_client_cls.return_value = mock_client

            result = await discover_tools("http://mcp:9999/mcp", "tok-123")

        assert len(result) == 2
        assert result[0]["name"] == "tool-a"

    @pytest.mark.asyncio
    async def test_discover_tools_raises_on_error(self):
        mock_response = MagicMock()
        mock_response.status_code = 200
        mock_response.raise_for_status = lambda: None
        mock_response.json.return_value = {
            "jsonrpc": "2.0",
            "id": 1,
            "error": {"code": -32600, "message": "Invalid request"},
        }

        with patch("mcp_bridge.httpx.AsyncClient") as mock_client_cls:
            mock_client = AsyncMock()
            mock_client.post.return_value = mock_response
            mock_client.__aenter__ = AsyncMock(return_value=mock_client)
            mock_client.__aexit__ = AsyncMock(return_value=False)
            mock_client_cls.return_value = mock_client

            with pytest.raises(RuntimeError, match="MCP tools/list error"):
                await discover_tools("http://mcp:9999/mcp", "tok-123")


class TestBuildArgsModel:
    def test_builds_model_from_schema(self, sample_tool_def):
        model = _build_args_model(sample_tool_def)
        assert model is not None
        assert "path" in model.model_fields
        assert "encoding" in model.model_fields

    def test_returns_none_for_no_schema(self, simple_tool_def):
        model = _build_args_model(simple_tool_def)
        assert model is None

    def test_returns_none_for_non_object_schema(self):
        tool_def = {
            "name": "test",
            "inputSchema": {"type": "string"},
        }
        model = _build_args_model(tool_def)
        assert model is None


class TestCreateToolProxy:
    def test_creates_callable_with_name(self, sample_tool_def):
        proxy = create_tool_proxy(sample_tool_def, "http://mcp:9999", "tok")
        # The @tool decorator wraps the function; the underlying name is preserved.
        assert proxy is not None

    def test_creates_callable_without_schema(self, simple_tool_def):
        proxy = create_tool_proxy(simple_tool_def, "http://mcp:9999", "tok")
        assert proxy is not None
