"""Tests for Message, Response, ShutdownReason types."""

from __future__ import annotations

from types import SimpleNamespace

from spring_voyage_agent.types import Message, Response, Sender, ShutdownReason


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
