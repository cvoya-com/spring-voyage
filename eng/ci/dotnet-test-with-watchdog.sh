#!/usr/bin/env bash
# dotnet test wrapper with a wall-clock watchdog (#2604).
#
# `Cvoya.Spring.Dapr.Tests` intermittently hangs under the full
# `dotnet test --solution` invocation: the suite normally runs in
# under a minute, but a rare condition leaves the test runner stuck
# with no output until the 30-minute job-level timeout kills it.
# When that happens the CI logs carry only "the operation was
# canceled" — there's nothing to diagnose against.
#
# This wrapper bounds the run with a configurable budget and, when
# the budget is exceeded, captures `dotnet-stack` thread dumps for
# every live testhost / dotnet child process before aborting. The
# wall-clock budget is well under the job-level timeout so the dump
# is preserved in the step's log instead of vanishing with the job.
#
# Usage: eng/ci/dotnet-test-with-watchdog.sh [dotnet-test-args...]
#
# Environment overrides:
#   TEST_BUDGET_SECONDS - total wall-clock budget. Default 900s (15
#                         min). Normal full-solution run is ~5 min;
#                         15 min comfortably covers the slowest
#                         legit run while staying well below CI's
#                         30-min job timeout.

set -uo pipefail

BUDGET="${TEST_BUDGET_SECONDS:-900}"

# Sentinel file the watchdog writes to when it has tripped the
# budget. The parent reads it after `wait` to distinguish a
# watchdog-killed test (forced non-zero exit) from a normal test
# exit — the test runner may exit 0 even after SIGTERM (it catches
# the signal and unwinds cleanly), so the child exit code alone is
# not a reliable failure signal in the budget-exceeded case. A file
# rather than a shell variable because the watchdog runs in a
# sub-shell and cannot mutate the parent's env.
WATCHDOG_TRIGGER_FILE="$(mktemp -t dotnet-test-watchdog-trigger-XXXXXX)"
trap 'rm -f "$WATCHDOG_TRIGGER_FILE"' EXIT

# --- helpers -----------------------------------------------------

capture_diagnostics() {
  local trigger="$1"

  echo
  echo "::group::Watchdog trigger: ${trigger}"

  echo "--- process tree (dotnet / testhost) ---"
  ps -ef | grep -E 'dotnet|testhost' | grep -v grep || true

  echo "--- dotnet-stack dumps ---"
  if ! command -v dotnet-stack >/dev/null 2>&1; then
    echo "Installing dotnet-stack on demand (not present on PATH)..."
    dotnet tool install -g dotnet-stack >/dev/null 2>&1 || \
      echo "dotnet-stack install failed; dumps unavailable."
    export PATH="$PATH:$HOME/.dotnet/tools"
  fi

  if command -v dotnet-stack >/dev/null 2>&1; then
    # testhost == the per-assembly child process MTP launches.
    # Plain `dotnet ... test` covers the in-process MTP runner too.
    for pid in $(pgrep -f 'testhost|dotnet.*test' || true); do
      # Skip the watchdog itself.
      [ "$pid" = "$$" ] && continue
      echo "--- pid $pid (cmd: $(ps -p "$pid" -o command= 2>/dev/null | head -c 200)) ---"
      dotnet-stack report --process-id "$pid" 2>&1 | head -500 || true
    done
  fi

  echo "::endgroup::"
}

kill_test_tree() {
  local pid="$1"

  # SIGTERM the child tree first so the runner can flush any final
  # output; SIGKILL 10s later if anything is still alive.
  pkill -TERM -P "$pid" 2>/dev/null || true
  kill -TERM "$pid" 2>/dev/null || true
  sleep 10
  pkill -KILL -P "$pid" 2>/dev/null || true
  kill -KILL "$pid" 2>/dev/null || true
}

# --- main --------------------------------------------------------

echo "Watchdog: budget=${BUDGET}s, cmd=dotnet test $*"

dotnet test "$@" &
TEST_PID=$!

# Background watchdog. Sleeps the budget, then — if the test
# process is still alive — captures diagnostics and tears the tree
# down. Self-terminates if the test finished first (the parent
# `kill` below).
(
  sleep "$BUDGET"
  if kill -0 "$TEST_PID" 2>/dev/null; then
    echo
    echo "::error::dotnet test exceeded ${BUDGET}s budget — likely the #2604 Dapr.Tests hang. Capturing diagnostics."
    echo "tripped" > "$WATCHDOG_TRIGGER_FILE"
    capture_diagnostics "wall-clock budget ${BUDGET}s exceeded"
    kill_test_tree "$TEST_PID"
  fi
) &
WATCHDOG_PID=$!

wait "$TEST_PID"
TEST_EXIT=$?

# Test finished within budget — silence the watchdog.
kill "$WATCHDOG_PID" 2>/dev/null || true
wait "$WATCHDOG_PID" 2>/dev/null || true

# Exit 124 (GNU `timeout` convention) when the watchdog tripped, so
# the CI step fails unambiguously regardless of the runner's own
# unwound exit code.
if [ -s "$WATCHDOG_TRIGGER_FILE" ]; then
  echo "Watchdog: budget exceeded; exiting 124 (test runner exited ${TEST_EXIT})."
  exit 124
fi

exit "$TEST_EXIT"
