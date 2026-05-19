"""
Token-bucket rate limiter for SDK-emitted telemetry events.

Issue #2493 §"Rate-limiting". A misbehaving agent calling
``context.report_progress`` 1000x/sec must not flood the OTLP ingest
plane. Each ``(subject_uuid, event_kind)`` pair gets its own token
bucket; events above the rate are dropped silently and at most one
warning is logged per 30 seconds per pair.

The defaults match the issue body:

  * sustained rate: 5 events/second
  * burst:          20 events

Both are env-overridable via ``SV_PROGRESS_RATE_LIMIT_RPS`` /
``SV_PROGRESS_RATE_LIMIT_BURST`` so an operator can tune without a
package rebuild.

The bucket is intentionally *coarse* — one bucket per (subject, kind)
pair, not per-span and not per-attribute. The point is to bound the
event volume reaching the OTLP plane, not to model fine-grained
fairness across concurrent threads within a single subject. Inside a
container the limiter is a process-global singleton; cross-container
fairness is a server-side problem the ingest can solve later if it
becomes necessary.
"""

from __future__ import annotations

import logging
import os
import threading
import time

logger = logging.getLogger("spring-voyage-agent-sdk.rate_limit")

_DEFAULT_RATE_PER_SECOND = 5.0
_DEFAULT_BURST = 20
_WARNING_INTERVAL_SECONDS = 30.0

_ENV_RATE = "SV_PROGRESS_RATE_LIMIT_RPS"
_ENV_BURST = "SV_PROGRESS_RATE_LIMIT_BURST"


def _env_float(name: str, default: float) -> float:
    value = os.environ.get(name)
    if value is None:
        return default
    try:
        parsed = float(value)
    except ValueError:
        logger.warning("Ignoring invalid float env var %s=%r; using %r", name, value, default)
        return default
    return parsed if parsed > 0 else default


def _env_int(name: str, default: int) -> int:
    value = os.environ.get(name)
    if value is None:
        return default
    try:
        parsed = int(value)
    except ValueError:
        logger.warning("Ignoring invalid int env var %s=%r; using %r", name, value, default)
        return default
    return parsed if parsed > 0 else default


class TokenBucket:
    """A single (subject, kind) bucket. Internal — see :class:`ProgressRateLimiter`.

    Implements the classic token bucket: tokens accumulate at
    ``rate_per_second`` up to ``burst``; each ``try_acquire`` consumes
    one token. The implementation is thread-safe so two concurrent
    threads sharing the same (subject, kind) pair cannot double-spend.
    """

    __slots__ = ("_rate", "_burst", "_tokens", "_last_refill", "_lock", "_last_warning_at")

    def __init__(self, rate_per_second: float, burst: int) -> None:
        self._rate = rate_per_second
        self._burst = float(burst)
        self._tokens = float(burst)
        self._last_refill = time.monotonic()
        self._last_warning_at = 0.0
        self._lock = threading.Lock()

    def try_acquire(self) -> bool:
        """Attempt to consume one token. Returns ``True`` on success."""
        with self._lock:
            now = time.monotonic()
            elapsed = max(0.0, now - self._last_refill)
            self._tokens = min(self._burst, self._tokens + elapsed * self._rate)
            self._last_refill = now

            if self._tokens >= 1.0:
                self._tokens -= 1.0
                return True
            return False

    def should_log_drop(self) -> bool:
        """Returns ``True`` at most once per ``_WARNING_INTERVAL_SECONDS``.

        Callers use this to throttle the "rate-limit fired" warning so a
        runaway emitter does not also flood the SDK's own log channel.
        """
        with self._lock:
            now = time.monotonic()
            if now - self._last_warning_at >= _WARNING_INTERVAL_SECONDS:
                self._last_warning_at = now
                return True
            return False


class ProgressRateLimiter:
    """Token-bucket limiter keyed by ``(subject_uuid, event_kind)``.

    Lazy: buckets are created on first access. Process-global by
    default — agent authors should not need to construct one. The SDK
    runtime owns the singleton instance.
    """

    def __init__(
        self,
        *,
        rate_per_second: float | None = None,
        burst: int | None = None,
    ) -> None:
        self._rate = rate_per_second if rate_per_second is not None else _env_float(_ENV_RATE, _DEFAULT_RATE_PER_SECOND)
        self._burst = burst if burst is not None else _env_int(_ENV_BURST, _DEFAULT_BURST)
        self._buckets: dict[tuple[str, str], TokenBucket] = {}
        self._buckets_lock = threading.Lock()

    @property
    def rate_per_second(self) -> float:
        return self._rate

    @property
    def burst(self) -> int:
        return self._burst

    def try_acquire(self, subject_uuid: str, event_kind: str) -> bool:
        """Attempt to emit one event for ``(subject_uuid, event_kind)``.

        Returns ``True`` if the event is allowed. On ``False`` the
        caller MUST drop the event; periodic warnings are emitted via
        :meth:`log_drop`.
        """
        bucket = self._bucket(subject_uuid, event_kind)
        return bucket.try_acquire()

    def log_drop(self, subject_uuid: str, event_kind: str) -> None:
        """Log a single warning per 30 s window for the (subject, kind) pair."""
        bucket = self._bucket(subject_uuid, event_kind)
        if bucket.should_log_drop():
            logger.warning(
                "Telemetry rate limit exceeded for subject=%s kind=%s; "
                "dropping events at rate>%.1f/s, burst=%d. Further drops "
                "for this pair will be silent for up to %.0fs.",
                subject_uuid,
                event_kind,
                self._rate,
                self._burst,
                _WARNING_INTERVAL_SECONDS,
            )

    def _bucket(self, subject_uuid: str, event_kind: str) -> TokenBucket:
        key = (subject_uuid, event_kind)
        with self._buckets_lock:
            bucket = self._buckets.get(key)
            if bucket is None:
                bucket = TokenBucket(self._rate, self._burst)
                self._buckets[key] = bucket
            return bucket


_DEFAULT_LIMITER: ProgressRateLimiter | None = None
_DEFAULT_LIMITER_LOCK = threading.Lock()


def default_limiter() -> ProgressRateLimiter:
    """Returns the process-wide default rate limiter, lazily constructed."""
    global _DEFAULT_LIMITER
    if _DEFAULT_LIMITER is None:
        with _DEFAULT_LIMITER_LOCK:
            if _DEFAULT_LIMITER is None:
                _DEFAULT_LIMITER = ProgressRateLimiter()
    return _DEFAULT_LIMITER


def reset_default_limiter() -> None:
    """Reset the process-wide default — exposed for tests only."""
    global _DEFAULT_LIMITER
    with _DEFAULT_LIMITER_LOCK:
        _DEFAULT_LIMITER = None
