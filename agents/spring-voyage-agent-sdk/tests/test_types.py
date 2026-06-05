"""Tests for Message, Response, ShutdownReason types."""

from __future__ import annotations

import textwrap
from types import SimpleNamespace

from spring_voyage_agent_sdk.types import (
    Envelope,
    Message,
    Response,
    Sender,
    ShutdownReason,
)


class TestShutdownReason:
    def test_all_values_defined(self):
        values = {r.value for r in ShutdownReason}
        assert values == {
            "requested",
            "idle_timeout",
            "policy",
            "error",
            "platform_restart",
            "unknown",
        }

    def test_string_equality(self):
        assert ShutdownReason.requested == "requested"


class TestMessageText:
    """Message.text concatenates text parts from various payload shapes."""

    def _make_message(self, parts) -> Message:
        return Message(
            thread_id="thr-1",
            message_id="msg-1",
            sender=Sender(kind="human", id="u1"),
            payload={"role": "user", "parts": parts},
            timestamp="2026-04-28T00:00:00Z",
        )

    def test_dict_text_parts(self):
        msg = self._make_message(
            [
                {"kind": "text", "text": "hello "},
                {"kind": "text", "text": "world"},
            ]
        )
        assert msg.text == "hello world"

    def test_dict_skips_non_text_parts(self):
        msg = self._make_message(
            [
                {"kind": "file", "file": "x"},
                {"kind": "text", "text": "ok"},
            ]
        )
        assert msg.text == "ok"

    def test_sdk_discriminated_union_parts(self):
        """a2a-sdk v0.3+ wraps parts in Part(root=TextPart(...))."""
        text_part = SimpleNamespace(root=SimpleNamespace(kind="text", text="sdk text"))
        file_part = SimpleNamespace(root=SimpleNamespace(kind="file"))
        msg = self._make_message([text_part, file_part])
        assert msg.text == "sdk text"

    def test_empty_parts_returns_empty_string(self):
        msg = self._make_message([])
        assert msg.text == ""

    def test_no_parts_key(self):
        msg = Message(
            thread_id="t",
            message_id="m",
            sender=Sender(kind="system", id="sys"),
            payload={"role": "system"},
            timestamp="",
        )
        assert msg.text == ""


class TestMessageOptionalFields:
    """ADR-0066: per-message MCP token and structured envelope are optional
    additive fields with backward-compatible defaults."""

    def _base(self, **overrides) -> Message:
        kwargs = dict(
            thread_id="t",
            message_id="m",
            sender=Sender(kind="agent", id="a"),
            payload={"role": "user", "parts": []},
            timestamp="",
        )
        kwargs.update(overrides)
        return Message(**kwargs)

    def test_defaults_backward_compatible(self):
        msg = self._base()
        assert msg.mcp_token is None
        assert msg.envelope is None

    def test_mcp_token_carried(self):
        msg = self._base(mcp_token="svmt_turn_42")
        assert msg.mcp_token == "svmt_turn_42"


class TestEnvelopeParsing:
    """Envelope.parse_* extracts the platform's fenced JSON appendix
    (ADR-0066 §3 / InboundEnvelopeBuilder)."""

    def _rendered(self, message_id: str, sender: str) -> str:
        return textwrap.dedent(
            f"""\
            You received a message.

            - from: {sender}
            - message_id: {message_id}
            - payload:

            write slot 3

            ```json
            {{
              "from": "{sender}",
              "to": ["agent:00000000000000000000000000000aaa"],
              "participants": ["agent:00000000000000000000000000000aaa", "{sender}"],
              "message_id": "{message_id}",
              "timestamp": "2026-06-04T00:00:00.0000000Z",
              "payload": {{"role": "user", "parts": [{{"kind": "text", "text": "write slot 3"}}]}}
            }}
            ```

            Decide what to do.
            """
        )

    def test_parse_latest_extracts_fields(self):
        env = Envelope.parse_latest(self._rendered("msg-7", "agent:writer"))
        assert env is not None
        assert env.from_address == "agent:writer"
        assert env.message_id == "msg-7"
        assert env.participants == [
            "agent:00000000000000000000000000000aaa",
            "agent:writer",
        ]

    def test_parse_latest_returns_most_recent_in_batch(self):
        batch = self._rendered("msg-1", "agent:writer") + "\n" + self._rendered("msg-2", "agent:factchecker")
        env = Envelope.parse_latest(batch)
        assert env is not None
        assert env.message_id == "msg-2"
        assert env.from_address == "agent:factchecker"

    def test_parse_all_returns_every_block(self):
        batch = self._rendered("msg-1", "agent:a") + "\n" + self._rendered("msg-2", "agent:b")
        envs = Envelope.parse_all(batch)
        assert [e.message_id for e in envs] == ["msg-1", "msg-2"]

    def test_no_block_returns_none(self):
        assert Envelope.parse_latest("just prose, no json block") is None

    def test_malformed_json_block_skipped(self):
        text = "```json\n{not valid json,,}\n```"
        assert Envelope.parse_latest(text) is None

    def test_block_without_from_is_ignored(self):
        text = '```json\n{"to": [], "message_id": "x"}\n```'
        assert Envelope.parse_latest(text) is None


class TestResponse:
    def test_text_chunk(self):
        r = Response(text="hello")
        assert r.text == "hello"
        assert r.error is None
        assert r.final is False

    def test_error_chunk(self):
        r = Response(error="something went wrong")
        assert r.error == "something went wrong"
        assert r.text is None

    def test_final_sentinel(self):
        r = Response(final=True)
        assert r.final is True
