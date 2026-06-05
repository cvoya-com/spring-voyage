"""Tests for the SDK's one-shot Claude completion (ADR-0066).

``llm.complete`` shells the co-hosted ``claude`` CLI; the subprocess is mocked
here so the tests exercise the argv shaping, result extraction, and the
error/timeout paths without a real CLI.
"""

from __future__ import annotations

import asyncio
import json
from pathlib import Path
from unittest.mock import patch

import pytest

from spring_voyage_agent_sdk import llm
from spring_voyage_agent_sdk.llm import ClaudeCompletionError


class _FakeProc:
    def __init__(self, *, stdout: bytes = b"", stderr: bytes = b"", returncode: int = 0):
        self._stdout = stdout
        self._stderr = stderr
        self.returncode = returncode
        self.killed = False

    async def communicate(self, _data: bytes | None = None):
        return self._stdout, self._stderr

    def kill(self):
        self.killed = True


def _exec_returning(proc):
    """Build a fake ``create_subprocess_exec`` that records argv (and the
    system-prompt-file content, read while the temp file still exists)."""

    async def _fake_exec(*args, **_kwargs):
        _fake_exec.argv = args
        if "--append-system-prompt-file" in args:
            sp_path = args[args.index("--append-system-prompt-file") + 1]
            _fake_exec.sp_content = Path(sp_path).read_text(encoding="utf-8")
        return proc

    return _fake_exec


class TestComplete:
    @pytest.mark.asyncio
    async def test_returns_result_field_and_base_argv(self):
        proc = _FakeProc(stdout=json.dumps({"result": "the answer"}).encode())
        fake = _exec_returning(proc)
        with patch("asyncio.create_subprocess_exec", fake):
            out = await llm.complete("hello")
        assert out == "the answer"
        assert fake.argv[:5] == (
            "claude",
            "--print",
            "--dangerously-skip-permissions",
            "--output-format",
            "json",
        )
        # No system prompt given and no default file → no append flag.
        assert "--append-system-prompt-file" not in fake.argv

    @pytest.mark.asyncio
    async def test_system_prompt_written_to_temp_file(self):
        proc = _FakeProc(stdout=b'{"result": "ok"}')
        fake = _exec_returning(proc)
        with patch("asyncio.create_subprocess_exec", fake):
            await llm.complete("hi", system_prompt="EXTRACT JSON ONLY")
        assert "--append-system-prompt-file" in fake.argv
        assert fake.sp_content == "EXTRACT JSON ONLY"

    @pytest.mark.asyncio
    async def test_default_system_prompt_file_appended_when_present(self, tmp_path):
        sp = tmp_path / "system-prompt.md"
        sp.write_text("persona", encoding="utf-8")
        proc = _FakeProc(stdout=b'{"result": "ok"}')
        fake = _exec_returning(proc)
        with patch("asyncio.create_subprocess_exec", fake):
            await llm.complete("hi", system_prompt_file=str(sp))
        assert list(fake.argv)[-2:] == ["--append-system-prompt-file", str(sp)]

    @pytest.mark.asyncio
    async def test_missing_default_file_is_skipped(self, tmp_path):
        proc = _FakeProc(stdout=b'{"result": "ok"}')
        fake = _exec_returning(proc)
        with patch("asyncio.create_subprocess_exec", fake):
            await llm.complete("hi", system_prompt_file=str(tmp_path / "absent.md"))
        assert "--append-system-prompt-file" not in fake.argv

    @pytest.mark.asyncio
    async def test_mcp_config_adds_flag(self):
        # ADR-0066 §6 Option B: with a config, Claude runs with the orchestration
        # tools available via --mcp-config.
        proc = _FakeProc(stdout=b'{"result": "ok"}')
        fake = _exec_returning(proc)
        with patch("asyncio.create_subprocess_exec", fake):
            await llm.complete("hi", mcp_config_path="/ws/.spring/orchestration-mcp.json")
        argv = list(fake.argv)
        assert "--mcp-config" in argv
        assert argv[argv.index("--mcp-config") + 1] == "/ws/.spring/orchestration-mcp.json"

    @pytest.mark.asyncio
    async def test_nonzero_exit_raises(self):
        proc = _FakeProc(stderr=b"boom", returncode=2)
        with patch("asyncio.create_subprocess_exec", _exec_returning(proc)):
            with pytest.raises(ClaudeCompletionError, match="exited 2"):
                await llm.complete("hi")

    @pytest.mark.asyncio
    async def test_missing_cli_raises(self):
        async def _missing(*_a, **_k):
            raise FileNotFoundError("no claude on PATH")

        with patch("asyncio.create_subprocess_exec", _missing):
            with pytest.raises(ClaudeCompletionError, match="not found"):
                await llm.complete("hi")

    @pytest.mark.asyncio
    async def test_timeout_kills_and_raises(self):
        class _HangProc(_FakeProc):
            async def communicate(self, _data=None):
                await asyncio.sleep(10)
                return b"", b""

        proc = _HangProc()
        with patch("asyncio.create_subprocess_exec", _exec_returning(proc)):
            with pytest.raises(ClaudeCompletionError, match="within"):
                await llm.complete("hi", timeout=0.05)
        assert proc.killed


class TestExtractResultText:
    def test_plain_json(self):
        assert llm._extract_result_text('{"result": "x"}') == "x"

    def test_fenced_or_pretty_json(self):
        assert llm._extract_result_text('{\n  "result": "multi\\nline"\n}') == "multi\nline"

    def test_non_json_returns_raw(self):
        assert llm._extract_result_text("just text") == "just text"

    def test_json_without_result_returns_raw(self):
        assert llm._extract_result_text('{"other": 1}') == '{"other": 1}'

    def test_empty(self):
        assert llm._extract_result_text("   ") == ""
