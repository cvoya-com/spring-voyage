# End-to-end CLI test scenarios

Shell-based scenarios that exercise the running SV v2 stack. Unlike the
unit/integration suite, these hit real containers and can catch wiring
regressions the mocked harness misses (see #311 for rationale).

## Prerequisites

- A running stack (Podman or `dapr run`-launched) reachable at `http://localhost`.
- `bash`, `curl`.
- `dotnet` (.NET 10 SDK) on PATH for the CLI-driven scenarios. To skip the
  build wait, override `SPRING_CLI` with a path to a prebuilt binary.

## Usage

```
./run.sh                              # all scenarios
./run.sh '03-*'                       # one
E2E_BASE_URL=http://sv:80 ./run.sh    # custom host
SPRING_CLI=/usr/local/bin/spring ./run.sh   # prebuilt CLI
SPRING_API_URL=http://sv:80 ./run.sh        # forwarded to `spring apply`
```

Each scenario exits 0 on pass, non-zero on any failure. The runner aggregates
results and exits non-zero if any scenario failed.

## CLI vs HTTP

Scenarios prefer the `spring` CLI when a viable subcommand exists, because the
CLI exercises three layers raw `curl` skips: the Kiota-generated client (which
breaks if `openapi.json` drifts), CLI argument parsing + output formatting, and
the `ApiTokenAuthHandler` Bearer-token path. Scenarios that have no CLI
counterpart stay on `e2e::http` with a TODO referencing the gap.

| # | Scenario | Driver | Why |
|---|----------|--------|-----|
| 01 | api-health | curl | Raw contract check; the point is to bypass the CLI/Kiota layer. |
| 02 | create-unit-scratch | CLI (`spring unit create`) | Covered by the CLI today. |
| 03 | create-unit-with-model | curl (TODO #315) | CLI lacks `--model`/`--color` flags. |
| 04 | create-unit-from-template | curl (TODO #316) | CLI has no `--from-template` (and `spring apply` skips the resolver/validator/binding-preview path that this scenario covers). |
| 05 | cli-version-and-help | CLI (`spring --help`) | Sanity-check the CLI starts up before heavier scenarios spend API time. |

## Authentication

The CLI reads its endpoint and token from `~/.spring/config.json` (see
`src/Cvoya.Spring.Cli/CliConfig.cs`). `spring apply` additionally honours
`SPRING_API_URL` as an override. When the API is launched with
`LocalDev=true`, no token is required and the harness can run without
configuring one.

## Adding a scenario

Create `scenarios/NN-short-name.sh`, source `_lib.sh`, use `e2e::cli` (or
`e2e::http` for raw checks), `e2e::expect_status`, `e2e::expect_contains`.
End with `e2e::summary`. Keep scenarios idempotent and cleaning up after
themselves where possible.

## Tracking

See issue #311 for the full roadmap and future scenario list. CLI gaps
discovered while porting scenarios live under #315 and #316.
