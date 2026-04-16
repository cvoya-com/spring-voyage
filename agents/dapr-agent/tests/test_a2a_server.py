"""Tests for a2a_server.py — Agent Card and application factory."""

from __future__ import annotations

from unittest.mock import MagicMock

from a2a_server import build_agent_card, create_a2a_app


class TestBuildAgentCard:
    def test_default_card_has_expected_fields(self):
        card = build_agent_card()
        assert card.name is not None
        assert "Dapr Agent" in card.name
        assert card.version == "1.0.0"
        assert len(card.skills) == 1
        assert card.skills[0].id == "dapr-agent-execute"

    def test_card_reflects_model_and_provider(self):
        card = build_agent_card(model="mistral:7b", provider="openai")
        assert "openai" in card.name
        assert "mistral:7b" in card.name
        assert "openai" in card.skills[0].tags

    def test_card_uses_custom_port(self):
        card = build_agent_card(port=7777)
        assert "7777" in card.url

    def test_card_reads_env_vars(self, monkeypatch):
        monkeypatch.setenv("SPRING_MODEL", "phi3:mini")
        monkeypatch.setenv("SPRING_LLM_PROVIDER", "local")
        monkeypatch.setenv("AGENT_PORT", "5555")

        card = build_agent_card()
        assert "phi3:mini" in card.name
        assert "local" in card.name
        assert "5555" in card.url


class TestCreateA2aApp:
    def test_creates_application(self):
        mock_executor = MagicMock()
        app = create_a2a_app(mock_executor, port=9999)
        assert app is not None

    def test_uses_default_port(self):
        mock_executor = MagicMock()
        app = create_a2a_app(mock_executor)
        assert app is not None
