# End-to-end CLI test scenarios

Shell-based scenarios that exercise the running SV v2 HTTP API. Unlike the
unit/integration suite, these hit real containers and can catch wiring
regressions the mocked harness misses (see #311 for rationale).

## Prerequisites

- A running stack (Podman or `dapr run`-launched) reachable at `http://localhost`.
- `curl`, `bash`.

## Usage

```
./run.sh                   # all scenarios
./run.sh '03-*'            # one
E2E_BASE_URL=http://sv:80 ./run.sh   # custom host
```

Each scenario exits 0 on pass, non-zero on any failure. The runner aggregates
results and exits non-zero if any scenario failed.

## Adding a scenario

Create `scenarios/NN-short-name.sh`, source `_lib.sh`, use `e2e::http`,
`e2e::expect_status`, `e2e::expect_contains`. End with `e2e::summary`. Keep
scenarios idempotent and cleaning up after themselves where possible.

## Tracking

See issue #311 for the full roadmap and future scenario list.
