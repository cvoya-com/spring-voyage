"""Unit tests for the structured specialist-reply parser (#3088)."""

from __future__ import annotations

import json

from orchestrator.reply import (
    STATUS_BLOCKED,
    STATUS_OK,
    body_from_payload,
    parse_specialist_reply,
)


def test_parses_bare_ok_object():
    r = parse_specialist_reply('{"status": "ok", "artifact": "# Story\\n\\nbody"}')
    assert r is not None and r.ok
    assert r.status == STATUS_OK
    assert r.artifact == "# Story\n\nbody"


def test_parses_blocked_with_reason():
    r = parse_specialist_reply(
        '{"status": "blocked", "reason": "the draft was not attached"}'
    )
    assert r is not None and not r.ok
    assert r.status == STATUS_BLOCKED
    assert r.reason == "the draft was not attached"


def test_parses_fenced_block_with_prose_around_it():
    text = 'Here is my reply:\n```json\n{"status":"ok","artifact":"done"}\n```\nthanks'
    r = parse_specialist_reply(text)
    assert r is not None and r.ok and r.artifact == "done"


def test_parses_bare_object_embedded_in_prose():
    text = 'Sure — {"status": "ok", "artifact": "the {nested} piece"} — all set.'
    r = parse_specialist_reply(text)
    assert r is not None and r.ok
    assert r.artifact == "the {nested} piece"


def test_artifact_round_trips_a_realistic_article():
    article = '# First Cup\n\n"The grinder," she said — heat, steam, a quiet {moment}.'
    payload = json.dumps({"status": "ok", "artifact": article})
    r = parse_specialist_reply(payload)
    assert r is not None and r.artifact == article


def test_unparseable_prose_returns_none():
    assert parse_specialist_reply("I can't do this, the draft is missing.") is None


def test_ok_with_empty_artifact_is_rejected():
    # A malformed "success" must not silently advance an empty piece.
    assert parse_specialist_reply('{"status": "ok", "artifact": ""}') is None
    assert parse_specialist_reply('{"status": "ok"}') is None


def test_unknown_status_is_rejected():
    assert parse_specialist_reply('{"status": "maybe", "artifact": "x"}') is None


def test_none_and_empty_return_none():
    assert parse_specialist_reply(None) is None
    assert parse_specialist_reply("") is None


def test_blocked_needs_no_artifact():
    r = parse_specialist_reply('{"status":"BLOCKED","reason":"out of remit"}')
    assert r is not None and not r.ok and r.reason == "out of remit"


# --- body_from_payload (the A2A payload → body extraction) -----------------


def test_body_from_content_wrapper():
    assert body_from_payload({"content": "the prose"}, "FALLBACK") == "the prose"


def test_body_from_text_wrapper():
    assert body_from_payload({"text": "hi"}, "FALLBACK") == "hi"


def test_body_from_string_payload():
    assert body_from_payload("raw string", "FALLBACK") == "raw string"


def test_body_from_structured_payload_reserialises_to_json():
    # The platform stored a JSON-bodied reply parsed into the payload object —
    # hand it back as JSON so parse_specialist_reply can read it (#3088).
    payload = {"status": "ok", "artifact": "# First Cup\n\nbody"}
    body = body_from_payload(payload, "PROSE FALLBACK")
    reply = parse_specialist_reply(body)
    assert reply is not None and reply.ok and reply.artifact == "# First Cup\n\nbody"


def test_body_falls_back_when_no_payload():
    assert body_from_payload(None, "FALLBACK") == "FALLBACK"
    assert body_from_payload({}, "FALLBACK") == "FALLBACK"
