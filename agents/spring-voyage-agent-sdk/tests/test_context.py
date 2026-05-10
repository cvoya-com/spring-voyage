"""Tests for IAgentContext loading.

Covers spec §2.2.1 (env var names) and §2.3 (SDK conformance:
missing required fields → fatal initialize failure).
"""

from __future__ import annotations

import json
import os
from unittest.mock import patch

import pytest

from spring_voyage_agent_sdk.context import ContextLoadError, IAgentContext

_REQUIRED_ENV = {
    "SPRING_TENANT_ID": "tenant_acme",
    "SPRING_AGENT_ID": "agent_be3",
    "SPRING_BUCKET2_URL": "https://api.example.com/api/v1/",
    "SPRING_BUCKET2_TOKEN": "svat_abc",
    "SPRING_LLM_PROVIDER_URL": "https://api.example.com/llm/",
    "SPRING_LLM_PROVIDER_TOKEN": "svlt_abc",
    "SPRING_MCP_URL": "https://api.example.com/mcp/",
    "SPRING_MCP_TOKEN": "svmt_abc",
    "SPRING_TELEMETRY_URL": "https://otel.example.com:4318",
    "SPRING_WORKSPACE_PATH": "/spring/workspace/",
    "SPRING_CONCURRENT_THREADS": "true",
}


def _patch_env(**overrides):
    """Return a context manager that sets the required env block + overrides."""
    env = {**_REQUIRED_ENV, **overrides}
    return patch.dict(os.environ, env, clear=False)


class TestIAgentContextLoad:
    def test_loads_all_required_fields(self):
        with _patch_env():
            ctx = IAgentContext.load()

        assert ctx.tenant_id == "tenant_acme"
        assert ctx.agent_id == "agent_be3"
        assert ctx.unit_id is None
        assert ctx.thread_id is None
        assert ctx.bucket2_url == "https://api.example.com/api/v1/"
        assert ctx.bucket2_token == "svat_abc"
        assert ctx.llm_provider_url == "https://api.example.com/llm/"
        assert ctx.llm_provider_token == "svlt_abc"
        assert ctx.mcp_url == "https://api.example.com/mcp/"
        assert ctx.mcp_token == "svmt_abc"
        assert ctx.telemetry_url == "https://otel.example.com:4318"
        assert ctx.telemetry_token is None
        assert ctx.workspace_path == "/spring/workspace/"
        assert ctx.concurrent_threads is True

    def test_unit_id_optional(self):
        with _patch_env(SPRING_UNIT_ID="unit_eng"):
            ctx = IAgentContext.load()
        assert ctx.unit_id == "unit_eng"

    def test_thread_id_populated_when_set(self):
        """SPRING_THREAD_ID is surfaced as context.thread_id when present (spec §2.2.1, #1357)."""
        with _patch_env(SPRING_THREAD_ID="thr_abc123"):
            ctx = IAgentContext.load()
        assert ctx.thread_id == "thr_abc123"

    def test_thread_id_is_none_when_absent(self, monkeypatch):
        """context.thread_id is None when SPRING_THREAD_ID is not set.

        This is the supervisor-restart path: the supervisor does not bind a
        thread id when restarting an agent (spec §2.2.1 — SPRING_THREAD_ID is
        optional; #1357).
        """
        for k, v in _REQUIRED_ENV.items():
            monkeypatch.setenv(k, v)
        monkeypatch.delenv("SPRING_THREAD_ID", raising=False)
        ctx = IAgentContext.load()
        assert ctx.thread_id is None

    def test_thread_id_absent_does_not_raise(self, monkeypatch):
        """SPRING_THREAD_ID absence must NOT raise ContextLoadError (optional field)."""
        for k, v in _REQUIRED_ENV.items():
            monkeypatch.setenv(k, v)
        monkeypatch.delenv("SPRING_THREAD_ID", raising=False)
        # Must not raise — thread_id is explicitly optional per spec §2.2.1.
        IAgentContext.load()

    def test_telemetry_token_optional(self):
        with _patch_env(SPRING_TELEMETRY_TOKEN="tok123"):
            ctx = IAgentContext.load()
        assert ctx.telemetry_token == "tok123"

    def test_concurrent_threads_false(self):
        with _patch_env(SPRING_CONCURRENT_THREADS="false"):
            ctx = IAgentContext.load()
        assert ctx.concurrent_threads is False

    def test_concurrent_threads_invalid_raises(self):
        with _patch_env(SPRING_CONCURRENT_THREADS="yes"):
            with pytest.raises(ContextLoadError, match="SPRING_CONCURRENT_THREADS"):
                IAgentContext.load()

    @pytest.mark.parametrize(
        "missing_var",
        [
            "SPRING_TENANT_ID",
            "SPRING_AGENT_ID",
            "SPRING_BUCKET2_URL",
            "SPRING_BUCKET2_TOKEN",
            "SPRING_LLM_PROVIDER_URL",
            "SPRING_LLM_PROVIDER_TOKEN",
            "SPRING_MCP_URL",
            "SPRING_MCP_TOKEN",
            "SPRING_TELEMETRY_URL",
            "SPRING_WORKSPACE_PATH",
            "SPRING_CONCURRENT_THREADS",
        ],
    )
    def test_missing_required_env_var_raises(self, missing_var, monkeypatch):
        """Every required env var missing → fatal ContextLoadError (spec §2.3)."""
        for k, v in _REQUIRED_ENV.items():
            monkeypatch.setenv(k, v)
        monkeypatch.delenv(missing_var, raising=False)

        with pytest.raises(ContextLoadError):
            IAgentContext.load()

    def test_agent_definition_absent_returns_empty(self):
        """When neither agent-definition.yaml nor .json exist, load() succeeds
        with an empty dict — matching local dev harness behaviour."""
        with _patch_env():
            # /spring/context/ almost certainly doesn't exist in CI.
            ctx = IAgentContext.load()
        assert ctx.agent_definition == {}

    def test_agent_definition_json_loaded(self, tmp_path):
        """agent-definition.json at the canonical path is parsed."""
        ctx_dir = tmp_path / "context"
        ctx_dir.mkdir()
        def_file = ctx_dir / "agent-definition.json"
        def_file.write_text(json.dumps({"id": "agent_be3", "instructions": "be helpful"}))

        import spring_voyage_agent_sdk.context as ctx_module

        orig_yaml = ctx_module._AGENT_DEF_YAML
        orig_json = ctx_module._AGENT_DEF_JSON

        ctx_module._AGENT_DEF_YAML = ctx_dir / "agent-definition.yaml"
        ctx_module._AGENT_DEF_JSON = def_file

        try:
            with _patch_env():
                ctx = IAgentContext.load()
            assert ctx.agent_definition["id"] == "agent_be3"
        finally:
            ctx_module._AGENT_DEF_YAML = orig_yaml
            ctx_module._AGENT_DEF_JSON = orig_json

    def test_tenant_config_absent_returns_empty(self):
        with _patch_env():
            ctx = IAgentContext.load()
        assert ctx.tenant_config == {}

    def test_tenant_config_loaded_when_present(self, tmp_path):
        """tenant-config.json is parsed when present."""
        ctx_dir = tmp_path / "context"
        ctx_dir.mkdir()
        cfg_file = ctx_dir / "tenant-config.json"
        cfg_file.write_text(json.dumps({"features": {"extended-context": True}}))

        import spring_voyage_agent_sdk.context as ctx_module

        orig_cfg = ctx_module._TENANT_CONFIG_JSON
        ctx_module._TENANT_CONFIG_JSON = cfg_file

        try:
            with _patch_env():
                ctx = IAgentContext.load()
            assert ctx.tenant_config["features"]["extended-context"] is True
        finally:
            ctx_module._TENANT_CONFIG_JSON = orig_cfg


class TestThreadWorkspace:
    """Per-thread on-disk workspace helper (ADR-0041, issue #2095)."""

    def test_returns_canonical_path_under_workspace(self, tmp_path):
        """Per ADR-0041 §"thread.id IS the session identifier", per-thread
        on-disk state lives under ``$SPRING_WORKSPACE_PATH/threads/<thread.id>/``.
        """
        with _patch_env(SPRING_WORKSPACE_PATH=str(tmp_path)):
            ctx = IAgentContext.load()

        path = ctx.thread_workspace("thr_abc123")
        assert path == tmp_path / "threads" / "thr_abc123"

    def test_creates_directory_on_first_access(self, tmp_path):
        """The helper MUST create the directory eagerly so authors can
        write into it without an extra ``mkdir`` call."""
        with _patch_env(SPRING_WORKSPACE_PATH=str(tmp_path)):
            ctx = IAgentContext.load()

        target = tmp_path / "threads" / "thr_xyz"
        assert not target.exists()
        path = ctx.thread_workspace("thr_xyz")
        assert path.exists()
        assert path.is_dir()

    def test_idempotent_on_repeat_access(self, tmp_path):
        """Repeat calls for the same thread_id MUST NOT raise (mkdir
        ``exist_ok=True``) and MUST NOT clobber existing files."""
        with _patch_env(SPRING_WORKSPACE_PATH=str(tmp_path)):
            ctx = IAgentContext.load()

        path = ctx.thread_workspace("thr_repeat")
        (path / "state.json").write_text('{"step": 1}')

        path_again = ctx.thread_workspace("thr_repeat")
        assert path_again == path
        assert (path / "state.json").read_text() == '{"step": 1}'

    def test_distinct_thread_ids_get_distinct_directories(self, tmp_path):
        with _patch_env(SPRING_WORKSPACE_PATH=str(tmp_path)):
            ctx = IAgentContext.load()

        a = ctx.thread_workspace("thr_a")
        b = ctx.thread_workspace("thr_b")
        assert a != b
        assert a.exists() and b.exists()

    def test_uses_workspace_path_from_env(self, tmp_path):
        """The helper reads ``workspace_path`` from the loaded context, which
        in turn reads ``SPRING_WORKSPACE_PATH`` from env (spec §2.2.1)."""
        custom = tmp_path / "custom-workspace"
        custom.mkdir()
        with _patch_env(SPRING_WORKSPACE_PATH=str(custom)):
            ctx = IAgentContext.load()

        path = ctx.thread_workspace("thr_env")
        assert path.is_relative_to(custom)
        assert path == custom / "threads" / "thr_env"

    @pytest.mark.parametrize("bad", ["", "   ", "\t"])
    def test_empty_thread_id_raises(self, tmp_path, bad):
        with _patch_env(SPRING_WORKSPACE_PATH=str(tmp_path)):
            ctx = IAgentContext.load()
        with pytest.raises(ValueError, match="thread_id"):
            ctx.thread_workspace(bad)

    def test_workspace_path_env_unset_surfaces_via_load(self, monkeypatch):
        """``SPRING_WORKSPACE_PATH`` is a required env var; absence raises
        at ``IAgentContext.load()`` (spec §2.2.1) — the helper is never
        reached. Asserting this keeps the helper's contract pinned to "the
        env var is always present by the time load() returned"."""
        for k, v in _REQUIRED_ENV.items():
            monkeypatch.setenv(k, v)
        monkeypatch.delenv("SPRING_WORKSPACE_PATH", raising=False)

        with pytest.raises(ContextLoadError, match="SPRING_WORKSPACE_PATH"):
            IAgentContext.load()
