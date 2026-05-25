"""
Pull-based agent bootstrap client (ADR-0055) — Python mirror of
``src/Cvoya.Spring.AgentSidecar/src/bootstrap.ts``.

On container start the SDK runtime pulls workspace files from the
worker-hosted endpoint ``GET $SPRING_BOOTSTRAP_URL`` with
``Authorization: Bearer $SPRING_BOOTSTRAP_TOKEN`` and materialises them
under ``$SPRING_WORKSPACE_PATH/``. This is what makes
``IAgentContext.system_prompt`` non-None for Spring Voyage Agent
containers: the launcher's ``ContributeBundleAsync`` writes
``.spring/system-prompt.md`` into the bundle and this client materialises
it before the SDK calls ``IAgentContext.load()``.

The TypeScript sidecar also runs a per-turn integrity check (re-pull on
divergence). The Python SDK exposes that as
:meth:`BootstrapFetcher.integrity_check_and_refresh` for future per-turn
wiring, but the SDK only calls :meth:`fetch_and_materialize` on startup
today (the Python ``initialize()`` hook reads ``context.system_prompt``
once and caches it, so a per-turn re-pull would be wasted without also
re-reading the cached prompt).

Issue #2734 — the launcher's bundle contribution and this client land
together so the contract is end-to-end working.
"""

from __future__ import annotations

import dataclasses
import hashlib
import json
import logging
import os
import urllib.error
import urllib.request
from pathlib import Path
from typing import Callable

logger = logging.getLogger("spring-voyage-agent-sdk.bootstrap")

# Env var carrying the absolute URL of the worker bootstrap endpoint
# (ADR-0055 §9). The Spring Voyage launcher + AgentContextBuilder stamp
# this on every dispatch.
BOOTSTRAP_URL_ENV_VAR = "SPRING_BOOTSTRAP_URL"

# Env var carrying the per-agent bootstrap bearer token (ADR-0055 §8).
BOOTSTRAP_TOKEN_ENV_VAR = "SPRING_BOOTSTRAP_TOKEN"

# Env var carrying the in-container path of the per-agent workspace
# mount where bundle files are materialised (D1 §2.2.1, ADR-0029).
WORKSPACE_PATH_ENV_VAR = "SPRING_WORKSPACE_PATH"

# Default request timeout for the bootstrap fetch. Generous because the
# worker may still be cold-starting when the agent container fires its
# first pull; tight enough that a misconfigured endpoint fails the
# container at startup rather than hanging forever.
_DEFAULT_FETCH_TIMEOUT_SECONDS = 30


class BootstrapError(RuntimeError):
    """Raised when the bootstrap fetch or materialisation fails.

    The SDK runtime treats this as a fatal startup error — the agent
    container starts with a half-populated workspace otherwise, and the
    bundle file the SDK then reads into ``IAgentContext.system_prompt``
    may be stale or missing entirely.
    """


@dataclasses.dataclass(frozen=True)
class BootstrapFile:
    """One file in the bootstrap bundle.

    Wire shape mirrors ``Cvoya.Spring.Core.Execution.AgentBootstrapFile``.
    """

    path: str
    sha256: str
    content: str


@dataclasses.dataclass(frozen=True)
class BootstrapBundle:
    """A bootstrap bundle the worker served.

    Wire shape mirrors ``Cvoya.Spring.Core.Execution.AgentBootstrapBundle``.
    """

    version: str
    issued_at: str
    files: tuple[BootstrapFile, ...]
    platform_file_hashes: dict[str, str]


# Caller-overridable HTTP fetcher. Receives (url, headers, timeout) and
# returns (status_code, response_etag_or_none, body_text). Tests inject a
# stub; production uses :func:`_default_fetch` which wraps urllib.
HttpFetch = Callable[[str, dict[str, str], float], tuple[int, str | None, str]]


def _default_fetch(url: str, headers: dict[str, str], timeout: float) -> tuple[int, str | None, str]:
    """Default HTTP fetcher backed by ``urllib.request``.

    Returns ``(status, etag, body)``. ``etag`` is the response
    ``ETag`` header verbatim (still quoted) or ``None`` when absent.
    """
    request = urllib.request.Request(url, method="GET")
    for k, v in headers.items():
        request.add_header(k, v)
    try:
        with urllib.request.urlopen(request, timeout=timeout) as response:
            body = response.read().decode("utf-8", errors="replace")
            etag = response.headers.get("ETag")
            return (response.status, etag, body)
    except urllib.error.HTTPError as exc:
        # urlopen raises HTTPError on 4xx/5xx; expose status so the
        # caller can distinguish 304 (cache hit) from a genuine
        # failure.
        body = exc.read().decode("utf-8", errors="replace") if exc.fp is not None else ""
        etag = exc.headers.get("ETag") if exc.headers else None
        return (exc.code, etag, body)


class BootstrapFetcher:
    """Pulls the agent's bootstrap bundle and materialises files under
    ``$SPRING_WORKSPACE_PATH``.

    Construct via :func:`create_from_env` rather than directly; the
    factory reads the contract env vars and raises a clear error when
    the launcher did not stamp them.
    """

    def __init__(
        self,
        url: str,
        token: str,
        workspace_path: str,
        *,
        fetch_impl: HttpFetch | None = None,
        timeout_seconds: float = _DEFAULT_FETCH_TIMEOUT_SECONDS,
    ) -> None:
        if not url:
            raise BootstrapError("BootstrapFetcher requires a non-empty url.")
        if not token:
            raise BootstrapError("BootstrapFetcher requires a non-empty token.")
        if not workspace_path:
            raise BootstrapError("BootstrapFetcher requires a non-empty workspace_path.")
        self._url = url
        self._token = token
        self._workspace_path = Path(workspace_path)
        self._fetch_impl: HttpFetch = fetch_impl or _default_fetch
        self._timeout = timeout_seconds
        self._cached_etag: str | None = None
        self._cached_files: dict[str, str] = {}
        self._cached_platform_hashes: dict[str, str] = {}

    @property
    def cached_version(self) -> str | None:
        """Return the cached bundle's version string (``sha256:<hex>``),
        or ``None`` when no bundle has been fetched yet. Exposed for
        tests and debug-level logging."""
        if self._cached_etag is None:
            return None
        return self._cached_etag.strip('"')

    def fetch_and_materialize(self) -> BootstrapBundle:
        """Pull the bundle from the worker and write every file onto
        the workspace volume.

        Must complete before the SDK calls ``IAgentContext.load()`` —
        the loader reads ``$SPRING_WORKSPACE_PATH/.spring/system-prompt.md``
        directly and would return ``system_prompt = None`` if the file
        is not there yet.

        Raises :class:`BootstrapError` on any failure (HTTP error,
        invalid response shape, unsafe file path) — the container
        should fail loudly rather than start with a half-populated
        workspace.
        """
        bundle = self._do_fetch(if_none_match=None)
        if bundle is None:
            # 304 on first fetch — only possible if a misbehaving proxy
            # injected an If-None-Match. Treat as a contract violation.
            raise BootstrapError("Bootstrap server returned 304 on first fetch — no If-None-Match was sent.")
        self._materialize_all(bundle)
        self._cache(bundle)
        logger.info(
            "Bootstrap bundle materialised: version=%s files=%d platform_files=%d",
            bundle.version,
            len(bundle.files),
            len(bundle.platform_file_hashes),
        )
        return bundle

    def integrity_check_and_refresh(self) -> "IntegrityCheckResult":
        """Per-turn refresh path (ADR-0055 §6).

        Always issues an ``If-None-Match`` fetch — the worker's
        content-addressable etag makes the 304 path one HTTP roundtrip
        with an empty body, while the 200 path delivers server-side
        updates that disk-divergence alone would never surface (e.g.
        the operator edited the agent's ``Instructions`` between
        turns).

        Result handling:

        - 304 — server confirms our cached bundle is current. Restore
          any platform file that drifted on disk from the cached bytes.
        - 200 — server returned a fresh bundle. Replace the cache and
          re-write every file.
        - fetch failure — best-effort: fall back to disk-only
          restoration from the existing cache so a transient network
          blip doesn't take down the turn.

        The Python SDK does not call this method today — the
        ``initialize()`` hook reads ``context.system_prompt`` once and
        caches it, so per-turn re-pulling would be wasted without also
        re-reading the cached prompt. The method exists so a future
        per-turn hook (or operator-driven refresh) can drop in without
        re-deriving the wire contract.
        """
        if self._cached_etag is None:
            return IntegrityCheckResult(
                checked=False,
                warning="no bundle cached; skipping integrity check",
            )

        try:
            refreshed = self._do_fetch(if_none_match=self._cached_etag)
        except BootstrapError as exc:
            restored = self._restore_diverged_from_cache()
            return IntegrityCheckResult(
                checked=True,
                restored=tuple(restored),
                warning=f"bootstrap fetch failed ({exc}); restored {len(restored)} diverged file(s) from cache",
            )

        if refreshed is None:
            # 304 — bundle unchanged; restore any on-disk drift from cache.
            restored = self._restore_diverged_from_cache()
            return IntegrityCheckResult(checked=True, restored=tuple(restored))

        # 200 — server returned a new bundle. Replace the cache and
        # re-write every file so the next read sees the fresh content.
        self._materialize_all(refreshed)
        self._cache(refreshed)
        return IntegrityCheckResult(
            checked=True,
            restored=tuple(self._cached_files.keys()),
        )

    def _do_fetch(self, *, if_none_match: str | None) -> BootstrapBundle | None:
        """Fetch the bundle. Returns ``None`` on 304, the parsed bundle
        on 200, raises :class:`BootstrapError` on anything else."""
        headers: dict[str, str] = {
            "Authorization": f"Bearer {self._token}",
            "Accept": "application/json",
        }
        if if_none_match:
            headers["If-None-Match"] = if_none_match

        try:
            status, etag, body = self._fetch_impl(self._url, headers, self._timeout)
        except Exception as exc:
            raise BootstrapError(f"bootstrap fetch {self._url} failed: {exc}") from exc

        if status == 304:
            return None
        if status < 200 or status >= 300:
            raise BootstrapError(f"bootstrap fetch {self._url} returned HTTP {status}: {_truncate(body)}")

        try:
            parsed = json.loads(body)
        except json.JSONDecodeError as exc:
            raise BootstrapError(f"bootstrap response is not valid JSON: {exc}") from exc

        bundle = _validate_bundle(parsed)
        # Cache the server-returned etag so per-turn refreshes carry
        # the same string verbatim — content-addressable hashing means
        # `"sha256:<hex>"` is the bundle's identity.
        if etag is None:
            etag = _build_etag(bundle.version)
        self._next_etag = etag
        return bundle

    def _cache(self, bundle: BootstrapBundle) -> None:
        # _do_fetch stashes the etag in _next_etag so we don't have to
        # plumb it through the return value (BootstrapBundle's shape
        # mirrors the wire, not the cached state).
        self._cached_etag = getattr(self, "_next_etag", _build_etag(bundle.version))
        self._cached_files = {f.path: f.content for f in bundle.files}
        self._cached_platform_hashes = dict(bundle.platform_file_hashes)

    def _materialize_all(self, bundle: BootstrapBundle) -> None:
        for f in bundle.files:
            self._materialize_one(f.path, f.content)

    def _materialize_one(self, relative_path: str, content: str) -> None:
        absolute = _resolve_safe_workspace_path(self._workspace_path, relative_path)
        absolute.parent.mkdir(parents=True, exist_ok=True)
        absolute.write_text(content, encoding="utf-8")

    def _restore_diverged_from_cache(self) -> list[str]:
        diverged = self._find_diverged_platform_files()
        restored: list[str] = []
        for path in diverged:
            cached = self._cached_files.get(path)
            if cached is None:
                continue
            try:
                self._materialize_one(path, cached)
                restored.append(path)
            except OSError as exc:
                logger.warning("Failed to restore %s from bootstrap cache: %s", path, exc)
        return restored

    def _find_diverged_platform_files(self) -> list[str]:
        diverged: list[str] = []
        for path, expected_hash in self._cached_platform_hashes.items():
            absolute = _resolve_safe_workspace_path(self._workspace_path, path)
            if not absolute.exists():
                diverged.append(path)
                continue
            actual = "sha256:" + hashlib.sha256(absolute.read_bytes()).hexdigest()
            if actual != expected_hash:
                diverged.append(path)
        return diverged


@dataclasses.dataclass(frozen=True)
class IntegrityCheckResult:
    """Result of one integrity-check pass. Used by future per-turn
    callers for structured logging — failures do not throw."""

    checked: bool
    restored: tuple[str, ...] = ()
    warning: str | None = None


def create_from_env(
    env: dict[str, str] | None = None,
    *,
    fetch_impl: HttpFetch | None = None,
) -> BootstrapFetcher | None:
    """Build a :class:`BootstrapFetcher` from ``os.environ``.

    Returns ``None`` when ``SPRING_BOOTSTRAP_URL`` is unset — the SDK
    skips the pull entirely (smoke-test harness, alternate launchers).
    Raises :class:`BootstrapError` when the launcher stamped a URL but
    not the matching token / workspace-path env vars (contract
    violation per ADR-0055 §9).
    """
    src = env if env is not None else os.environ

    url = src.get(BOOTSTRAP_URL_ENV_VAR)
    if not url:
        return None
    token = src.get(BOOTSTRAP_TOKEN_ENV_VAR)
    if not token:
        raise BootstrapError(
            f"{BOOTSTRAP_URL_ENV_VAR} is set but {BOOTSTRAP_TOKEN_ENV_VAR} is empty. "
            "The launcher must stamp both together (ADR-0055 §9)."
        )
    workspace_path = src.get(WORKSPACE_PATH_ENV_VAR)
    if not workspace_path:
        raise BootstrapError(
            f"{BOOTSTRAP_URL_ENV_VAR} is set but {WORKSPACE_PATH_ENV_VAR} is empty. "
            "The launcher must stamp the workspace mount path before the SDK can materialise files."
        )
    return BootstrapFetcher(
        url=url,
        token=token,
        workspace_path=workspace_path,
        fetch_impl=fetch_impl,
    )


def _validate_bundle(value: object) -> BootstrapBundle:
    if not isinstance(value, dict):
        raise BootstrapError("bootstrap response is not a JSON object")
    version = value.get("version")
    issued_at = value.get("issuedAt")
    files = value.get("files")
    platform_hashes = value.get("platformFileHashes")

    if not isinstance(version, str) or not version.startswith("sha256:"):
        raise BootstrapError("bootstrap response: missing or malformed `version` (expected `sha256:<hex>`)")
    if not isinstance(issued_at, str):
        raise BootstrapError("bootstrap response: missing or malformed `issuedAt`")
    if not isinstance(files, list):
        raise BootstrapError("bootstrap response: `files` must be an array")
    if not isinstance(platform_hashes, dict):
        raise BootstrapError("bootstrap response: `platformFileHashes` must be an object")

    validated_files: list[BootstrapFile] = []
    for entry in files:
        if not isinstance(entry, dict):
            raise BootstrapError("bootstrap response: each file must be an object")
        path = entry.get("path")
        sha = entry.get("sha256")
        content = entry.get("content")
        if not isinstance(path, str) or not path:
            raise BootstrapError("bootstrap response: file.path must be a non-empty string")
        if not isinstance(sha, str):
            raise BootstrapError("bootstrap response: file.sha256 must be a string")
        if not isinstance(content, str):
            raise BootstrapError("bootstrap response: file.content must be a string")
        validated_files.append(BootstrapFile(path=path, sha256=sha, content=content))

    validated_hashes: dict[str, str] = {}
    for k, v in platform_hashes.items():
        if not isinstance(v, str):
            raise BootstrapError(f"bootstrap response: platformFileHashes[{k}] must be a string")
        validated_hashes[k] = v

    return BootstrapBundle(
        version=version,
        issued_at=issued_at,
        files=tuple(validated_files),
        platform_file_hashes=validated_hashes,
    )


def _resolve_safe_workspace_path(workspace_root: Path, relative: str) -> Path:
    """Resolve ``relative`` under ``workspace_root`` rejecting absolute
    paths, drive letters, and ``..`` traversal. Mirrors the C#
    ``WorkspaceMaterializer.SanitizeRelativePath`` and TS
    ``resolveSafeWorkspacePath`` behaviour."""
    if not relative:
        raise BootstrapError("bootstrap file path must not be empty")
    normalised = relative.replace("\\", "/")
    if normalised.startswith("/") or ":" in normalised:
        raise BootstrapError(f"bootstrap file path must be relative; got: {relative}")
    root = workspace_root.resolve()
    candidate = (root / normalised).resolve()
    if candidate != root and root not in candidate.parents:
        raise BootstrapError(f"bootstrap file path escapes the workspace root: {relative}")
    return candidate


def _build_etag(version: str) -> str:
    return f'"{version}"'


def _truncate(text: str, *, limit: int = 200) -> str:
    if len(text) <= limit:
        return text
    return text[:limit] + "…"
