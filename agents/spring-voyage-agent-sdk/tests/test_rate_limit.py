"""Tests for the SDK's token-bucket rate limiter (#2493)."""

from __future__ import annotations

import logging
import time

import pytest

from spring_voyage_agent_sdk.rate_limit import (
    ProgressRateLimiter,
    TokenBucket,
    reset_default_limiter,
)


class TestTokenBucket:
    def test_burst_allows_up_to_burst_calls_immediately(self):
        bucket = TokenBucket(rate_per_second=1.0, burst=5)
        assert bucket.try_acquire()
        assert bucket.try_acquire()
        assert bucket.try_acquire()
        assert bucket.try_acquire()
        assert bucket.try_acquire()

    def test_burst_exhausted_then_rate_limited(self):
        bucket = TokenBucket(rate_per_second=0.01, burst=2)
        assert bucket.try_acquire()
        assert bucket.try_acquire()
        # Third call exhausts the burst — rate is too slow to refill in
        # a synchronous test.
        assert not bucket.try_acquire()

    def test_refill_after_wait(self):
        bucket = TokenBucket(rate_per_second=100.0, burst=1)
        assert bucket.try_acquire()
        assert not bucket.try_acquire()
        time.sleep(0.05)  # ~5 tokens worth at 100/s, capped at burst=1
        assert bucket.try_acquire()


class TestProgressRateLimiter:
    def test_per_pair_isolation(self):
        limiter = ProgressRateLimiter(rate_per_second=0.01, burst=2)
        # Each (subject, kind) pair gets its own bucket.
        assert limiter.try_acquire("subject-a", "progress")
        assert limiter.try_acquire("subject-a", "progress")
        assert not limiter.try_acquire("subject-a", "progress")
        # Different subject — fresh bucket.
        assert limiter.try_acquire("subject-b", "progress")
        # Different kind on same subject — fresh bucket.
        assert limiter.try_acquire("subject-a", "tool_call")

    def test_thousand_per_second_capped_at_burst_plus_sustained(self):
        """Acceptance criterion: 1000x/sec → ≤ burst + sustained × duration."""
        rate = 5.0
        burst = 20
        limiter = ProgressRateLimiter(rate_per_second=rate, burst=burst)

        # Simulate ~0.1 s of bursty calls — at 1000/s that's 100 attempts.
        # Expected upper bound: burst (20) + rate × 0.1 (0.5) ≈ 20-21.
        start = time.monotonic()
        accepted = 0
        attempts = 0
        while time.monotonic() - start < 0.1:
            attempts += 1
            if limiter.try_acquire("subject", "progress"):
                accepted += 1

        assert attempts > 50  # confirms the test actually iterated fast
        # Upper bound: burst + sustained × actual elapsed (≤ 0.15 with overhead).
        elapsed = time.monotonic() - start
        upper_bound = burst + int(rate * elapsed) + 2  # +2 slack for monotonic drift
        assert accepted <= upper_bound, (
            f"accepted={accepted} attempts={attempts} elapsed={elapsed:.3f}s upper_bound={upper_bound}"
        )

    def test_warning_throttled(self, caplog: pytest.LogCaptureFixture):
        limiter = ProgressRateLimiter(rate_per_second=0.01, burst=1)
        # Drain the burst.
        limiter.try_acquire("subject", "progress")
        # First drop logs.
        with caplog.at_level(logging.WARNING, logger="spring-voyage-agent-sdk.rate_limit"):
            assert not limiter.try_acquire("subject", "progress")
            limiter.log_drop("subject", "progress")
        warnings = [r for r in caplog.records if r.levelno == logging.WARNING]
        assert len(warnings) == 1
        # Second drop within 30 s does NOT log.
        warnings_before = len(warnings)
        with caplog.at_level(logging.WARNING, logger="spring-voyage-agent-sdk.rate_limit"):
            limiter.log_drop("subject", "progress")
        warnings_after = [r for r in caplog.records if r.levelno == logging.WARNING]
        assert len(warnings_after) == warnings_before

    def test_env_overrides(self, monkeypatch: pytest.MonkeyPatch):
        monkeypatch.setenv("SV_PROGRESS_RATE_LIMIT_RPS", "12.5")
        monkeypatch.setenv("SV_PROGRESS_RATE_LIMIT_BURST", "99")
        reset_default_limiter()
        from spring_voyage_agent_sdk.rate_limit import default_limiter

        limiter = default_limiter()
        assert limiter.rate_per_second == 12.5
        assert limiter.burst == 99
        reset_default_limiter()
