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
    # Plain `dotnet exec` covers the in-process MTP runner too.
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

exit "$TEST_EXIT"
