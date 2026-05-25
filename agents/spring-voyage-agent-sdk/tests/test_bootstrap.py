"""Tests for the SDK's pull-based bootstrap client (ADR-0055, #2734).

The client is the Python mirror of
``src/Cvoya.Spring.AgentSidecar/src/bootstrap.ts``. It pulls the bundle
from the worker on container start and materialises every file under
``$SPRING_WORKSPACE_PATH/`` so the SDK's ``IAgentContext.load()`` can
read ``.spring/system-prompt.md`` into ``context.system_prompt`` (spec
§2.2.2).
"""

from __future__ import annotations

import json
from pathlib import Path

import pytest

from spring_voyage_agent_sdk.bootstrap import (
    BOOTSTRAP_TOKEN_ENV_VAR,
    BOOTSTRAP_URL_ENV_VAR,
    WORKSPACE_PATH_ENV_VAR,
    BootstrapError,
    BootstrapFetcher,
    create_from_env,
)


def _bundle_response(
    *,
    version: str = "sha256:abc123",
    files: list[dict[str, str]] | None = None,
    platform_hashes: dict[str, str] | None = None,
) -> str:
    """Build a JSON bundle response body matching the C# DTO shape."""
    return json.dumps(
        {
            "version": version,
            "issuedAt": "2026-05-22T12:00:00Z",
            "files": files if files is not None else [],
            "platformFileHashes": platform_hashes if platform_hashes is not None else {},
        }
    )


def _system_prompt_file(content: str = "You are a helpful assistant.") -> dict[str, str]:
    """Build a bootstrap file entry for the canonical system-prompt path."""
    return {
        "path": ".spring/system-prompt.md",
        "sha256": "sha256:fake-hash-not-validated-on-fetch",
        "content": content,
    }


class _FakeFetch:
    """Recorded HTTP fetcher for tests. Returns canned responses by URL."""

    def __init__(self, responses: list[tuple[int, str | None, str]]) -> None:
        self._responses = list(responses)
        self.calls: list[tuple[str, dict[str, str], float]] = []

    def __call__(self, url: str, headers: dict[str, str], timeout: float) -> tuple[int, str | None, str]:
        self.calls.append((url, dict(headers), timeout))
        if not self._responses:
            raise AssertionError("FakeFetch ran out of canned responses")
        return self._responses.pop(0)


class TestFetchAndMaterialize:
    def test_writes_platform_prompt_to_workspace(self, tmp_path: Path) -> None:
        """The canonical happy path: bundle carries
        `.spring/system-prompt.md`, the client writes it under the
        workspace mount so IAgentContext.load() can read it."""
        body = _bundle_response(
            files=[_system_prompt_file("You are helpful.")],
            platform_hashes={".spring/system-prompt.md": "sha256:fake"},
        )
        fetcher = BootstrapFetcher(
            url="http://worker/v1/bootstrap/agents/abc",
            token="bearer-xyz",
            workspace_path=str(tmp_path),
            fetch_impl=_FakeFetch([(200, '"sha256:abc123"', body)]),
        )

        bundle = fetcher.fetch_and_materialize()

        # The file is on disk at the workspace-relative path.
        on_disk = tmp_path / ".spring" / "system-prompt.md"
        assert on_disk.exists()
        assert on_disk.read_text() == "You are helpful."
        # The bundle return-value mirrors the wire response.
        assert bundle.version == "sha256:abc123"
        assert len(bundle.files) == 1
        assert bundle.files[0].path == ".spring/system-prompt.md"

    def test_sends_bearer_auth_and_accept_header(self, tmp_path: Path) -> None:
        """The fetch presents the Authorization bearer (ADR-0055 §8)
        and the ``Accept: application/json`` header per the wire
        contract (ADR-0055 §3)."""
        fake = _FakeFetch([(200, None, _bundle_response())])
        BootstrapFetcher(
            url="http://worker/v1/bootstrap/agents/abc",
            token="bearer-xyz",
            workspace_path=str(tmp_path),
            fetch_impl=fake,
        ).fetch_and_materialize()

        url, headers, _ = fake.calls[0]
        assert url == "http://worker/v1/bootstrap/agents/abc"
        assert headers["Authorization"] == "Bearer bearer-xyz"
        assert headers["Accept"] == "application/json"
        # First fetch must NOT send If-None-Match (defensive: a 304 on
        # first fetch would be a contract violation).
        assert "If-None-Match" not in headers

    def test_creates_parent_directories(self, tmp_path: Path) -> None:
        """File paths inside the bundle may include intermediate
        directories (e.g. ``.spring/connectors/<slug>/<file>``); the
        materialiser must create them on demand."""
        body = _bundle_response(
            files=[
                {
                    "path": ".spring/connectors/github/binding.json",
                    "sha256": "sha256:fake",
                    "content": "{}",
                }
            ]
        )
        BootstrapFetcher(
            url="http://worker/v1/bootstrap/agents/abc",
            token="bearer-xyz",
            workspace_path=str(tmp_path),
            fetch_impl=_FakeFetch([(200, None, body)]),
        ).fetch_and_materialize()

        assert (tmp_path / ".spring" / "connectors" / "github" / "binding.json").exists()

    def test_rejects_absolute_path(self, tmp_path: Path) -> None:
        """Absolute paths inside the bundle are rejected — workspace
        path safety mirrors the C# materialiser."""
        body = _bundle_response(
            files=[
                {
                    "path": "/etc/passwd",
                    "sha256": "sha256:fake",
                    "content": "x",
                }
            ]
        )

        with pytest.raises(BootstrapError, match="must be relative"):
            BootstrapFetcher(
                url="http://worker/v1/bootstrap/agents/abc",
                token="bearer-xyz",
                workspace_path=str(tmp_path),
                fetch_impl=_FakeFetch([(200, None, body)]),
            ).fetch_and_materialize()

    def test_rejects_traversal(self, tmp_path: Path) -> None:
        """`..` traversal escapes the workspace and is rejected."""
        body = _bundle_response(
            files=[
                {
                    "path": "../secret.txt",
                    "sha256": "sha256:fake",
                    "content": "x",
                }
            ]
        )

        with pytest.raises(BootstrapError, match="escapes the workspace"):
            BootstrapFetcher(
                url="http://worker/v1/bootstrap/agents/abc",
                token="bearer-xyz",
                workspace_path=str(tmp_path),
                fetch_impl=_FakeFetch([(200, None, body)]),
            ).fetch_and_materialize()

    def test_raises_on_http_error(self, tmp_path: Path) -> None:
        """A non-2xx response is a fatal startup error — the SDK
        surfaces this as ``initialize_done`` never set, blocking
        on_message."""
        with pytest.raises(BootstrapError, match="HTTP 500"):
            BootstrapFetcher(
                url="http://worker/v1/bootstrap/agents/abc",
                token="bearer-xyz",
                workspace_path=str(tmp_path),
                fetch_impl=_FakeFetch([(500, None, "boom")]),
            ).fetch_and_materialize()

    def test_raises_on_first_fetch_304(self, tmp_path: Path) -> None:
        """A 304 on first fetch (no If-None-Match sent) is a contract
        violation — fail loudly rather than starting with an empty
        workspace."""
        with pytest.raises(BootstrapError, match="304 on first fetch"):
            BootstrapFetcher(
                url="http://worker/v1/bootstrap/agents/abc",
                token="bearer-xyz",
                workspace_path=str(tmp_path),
                fetch_impl=_FakeFetch([(304, None, "")]),
            ).fetch_and_materialize()

    def test_raises_on_invalid_json(self, tmp_path: Path) -> None:
        with pytest.raises(BootstrapError, match="not valid JSON"):
            BootstrapFetcher(
                url="http://worker/v1/bootstrap/agents/abc",
                token="bearer-xyz",
                workspace_path=str(tmp_path),
                fetch_impl=_FakeFetch([(200, None, "not json")]),
            ).fetch_and_materialize()

    def test_raises_on_missing_version_field(self, tmp_path: Path) -> None:
        body = json.dumps(
            {
                # version absent
                "issuedAt": "2026-05-22T12:00:00Z",
                "files": [],
                "platformFileHashes": {},
            }
        )
        with pytest.raises(BootstrapError, match="version"):
            BootstrapFetcher(
                url="http://worker/v1/bootstrap/agents/abc",
                token="bearer-xyz",
                workspace_path=str(tmp_path),
                fetch_impl=_FakeFetch([(200, None, body)]),
            ).fetch_and_materialize()

    def test_caches_etag_for_subsequent_refresh(self, tmp_path: Path) -> None:
        """The fetcher remembers the server-returned ETag so a
        downstream integrity-check uses ``If-None-Match`` against it."""
        body = _bundle_response()
        fetcher = BootstrapFetcher(
            url="http://worker/v1/bootstrap/agents/abc",
            token="bearer-xyz",
            workspace_path=str(tmp_path),
            fetch_impl=_FakeFetch([(200, '"sha256:abc123"', body)]),
        )
        fetcher.fetch_and_materialize()
        assert fetcher.cached_version == "sha256:abc123"


class TestIntegrityCheckAndRefresh:
    """The per-turn refresh path. Not wired into the SDK runtime today
    (the Python initialize() reads system_prompt once and caches it),
    but the method exists for future per-turn hooks per ADR-0055 §6."""

    def test_304_keeps_cached_files(self, tmp_path: Path) -> None:
        # First fetch primes the cache.
        first_body = _bundle_response(files=[_system_prompt_file("v1")])
        fake = _FakeFetch(
            [
                (200, '"sha256:abc"', first_body),
                (304, None, ""),
            ]
        )
        fetcher = BootstrapFetcher(
            url="http://worker/v1/bootstrap/agents/abc",
            token="bearer-xyz",
            workspace_path=str(tmp_path),
            fetch_impl=fake,
        )
        fetcher.fetch_and_materialize()

        result = fetcher.integrity_check_and_refresh()

        assert result.checked is True
        assert result.warning is None
        # Second fetch sent If-None-Match with the cached etag.
        _, second_headers, _ = fake.calls[1]
        assert second_headers["If-None-Match"] == '"sha256:abc"'

    def test_200_replaces_cache_and_files(self, tmp_path: Path) -> None:
        first_body = _bundle_response(
            version="sha256:v1",
            files=[_system_prompt_file("v1 content")],
        )
        second_body = _bundle_response(
            version="sha256:v2",
            files=[_system_prompt_file("v2 content")],
        )
        fake = _FakeFetch(
            [
                (200, '"sha256:v1"', first_body),
                (200, '"sha256:v2"', second_body),
            ]
        )
        fetcher = BootstrapFetcher(
            url="http://worker/v1/bootstrap/agents/abc",
            token="bearer-xyz",
            workspace_path=str(tmp_path),
            fetch_impl=fake,
        )
        fetcher.fetch_and_materialize()
        assert (tmp_path / ".spring" / "system-prompt.md").read_text() == "v1 content"

        fetcher.integrity_check_and_refresh()

        assert fetcher.cached_version == "sha256:v2"
        assert (tmp_path / ".spring" / "system-prompt.md").read_text() == "v2 content"

    def test_no_cache_skips_check(self, tmp_path: Path) -> None:
        fetcher = BootstrapFetcher(
            url="http://worker/v1/bootstrap/agents/abc",
            token="bearer-xyz",
            workspace_path=str(tmp_path),
            fetch_impl=_FakeFetch([]),
        )
        result = fetcher.integrity_check_and_refresh()
        assert result.checked is False
        assert result.warning is not None


class TestCreateFromEnv:
    def test_returns_none_when_url_unset(self, monkeypatch: pytest.MonkeyPatch) -> None:
        """No bootstrap URL → no fetcher. The SDK skips the pull
        (smoke-test harness, alternate launchers, local-dev runs)."""
        monkeypatch.delenv(BOOTSTRAP_URL_ENV_VAR, raising=False)
        assert create_from_env() is None

    def test_raises_when_url_set_but_token_missing(self, monkeypatch: pytest.MonkeyPatch) -> None:
        """ADR-0055 §9 contract: the launcher must stamp URL and token
        together. Partial config is a launcher bug, surface it."""
        monkeypatch.setenv(BOOTSTRAP_URL_ENV_VAR, "http://worker/v1/bootstrap/agents/abc")
        monkeypatch.delenv(BOOTSTRAP_TOKEN_ENV_VAR, raising=False)
        monkeypatch.setenv(WORKSPACE_PATH_ENV_VAR, "/spring/workspace")
        with pytest.raises(BootstrapError, match=BOOTSTRAP_TOKEN_ENV_VAR):
            create_from_env()

    def test_raises_when_url_set_but_workspace_missing(self, monkeypatch: pytest.MonkeyPatch) -> None:
        monkeypatch.setenv(BOOTSTRAP_URL_ENV_VAR, "http://worker/v1/bootstrap/agents/abc")
        monkeypatch.setenv(BOOTSTRAP_TOKEN_ENV_VAR, "tok")
        monkeypatch.delenv(WORKSPACE_PATH_ENV_VAR, raising=False)
        with pytest.raises(BootstrapError, match=WORKSPACE_PATH_ENV_VAR):
            create_from_env()

    def test_builds_fetcher_when_all_env_present(self, monkeypatch: pytest.MonkeyPatch, tmp_path: Path) -> None:
        monkeypatch.setenv(BOOTSTRAP_URL_ENV_VAR, "http://worker/v1/bootstrap/agents/abc")
        monkeypatch.setenv(BOOTSTRAP_TOKEN_ENV_VAR, "tok")
        monkeypatch.setenv(WORKSPACE_PATH_ENV_VAR, str(tmp_path))

        fetcher = create_from_env()
        assert fetcher is not None
