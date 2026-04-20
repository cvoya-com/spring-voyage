# Agent Runtimes — Operator Guide

> Practical CLI workflows for installing, configuring, and maintaining agent runtimes on a tenant. Audience: operators with some ops background but no prior Spring Voyage context.

Agent runtimes are the plugin layer that bundles a conversation tool (Claude Code CLI, OpenAI SDK, dapr-agent) with a compatible LLM backend, its credential schema, and its model catalog. The OSS core ships four: `claude`, `openai`, `google`, `ollama`. Each runtime is registered in the host at startup but becomes _visible_ to a tenant only after an install row exists.

**Where this fits:** on a fresh OSS deployment the Worker host's bootstrap installs every registered runtime onto the default tenant automatically, so you can skip straight to "validating credentials." You only reach for `install` / `uninstall` when you want to curate the list (e.g. hide Ollama in a cloud-only deployment).

All commands below assume you've authenticated the CLI (`spring auth login`). Every mutation below is **CLI-only** — the portal may render read-only banners of this data, but writes come through `spring`.

## Listing installed runtimes

```
$ spring agent-runtime list
id       displayName  toolKind        defaultModel         models
claude   Claude       claude-code-cli claude-sonnet-4-5    claude-sonnet-4-5,claude-opus-4-1
google   Google       dapr-agent      gemini-2.0-flash     gemini-2.0-flash
ollama   Ollama       dapr-agent      llama3.2             llama3.2
openai   OpenAI       dapr-agent      gpt-4o               gpt-4o,gpt-4o-mini
```

`list` reads tenant-installed rows; on a fresh deployment that's every registered runtime. Pipe through `-o json` for script-friendly output.

## Inspecting a runtime

```
$ spring agent-runtime show claude
id       displayName  toolKind        defaultModel       models
claude   Claude       claude-code-cli claude-sonnet-4-5  claude-sonnet-4-5,claude-opus-4-1
```

A 404 means the runtime is not installed on the current tenant — re-install with `spring agent-runtime install claude`.

## Installing or refreshing a runtime

```
$ spring agent-runtime install claude
```

Install is idempotent: re-running with no flags is a no-op against operator-edited config. Flags override:

```
$ spring agent-runtime install openai \
    --model gpt-4o \
    --model gpt-4o-mini \
    --default-model gpt-4o \
    --base-url https://openai-proxy.example.com
```

- `--model <id>` — repeatable. Pins the tenant's configured list (replaces what was there).
- `--default-model <id>` — pre-select in the wizard.
- `--base-url <url>` — for Ollama / OpenAI-compatible gateways.

**Unknown runtime id** → `spring` exits 1 with: `Runtime '<id>' is not registered with the host.` Valid ids match projects under `src/Cvoya.Spring.AgentRuntimes.*` in the host.

## Setting or updating the model list

Three sugar verbs over `PATCH /config`:

```
$ spring agent-runtime models set claude claude-sonnet-4-5,claude-opus-4-1
$ spring agent-runtime models add claude claude-opus-4-1
$ spring agent-runtime models remove claude claude-opus-4-1
```

- `set` replaces the list.
- `add` appends (no-op if already present, case-insensitive).
- `remove` drops the id.

```
$ spring agent-runtime models list claude
id                  displayName        contextWindow
claude-sonnet-4-5   Claude Sonnet 4.5  200000
claude-opus-4-1     Claude Opus 4.1    200000
```

## Setting non-model config

```
$ spring agent-runtime config set claude defaultModel=claude-opus-4-1
$ spring agent-runtime config set ollama baseUrl=http://ollama.internal:11434
$ spring agent-runtime config set ollama baseUrl=        # clears the field
```

Supported keys: `defaultModel`, `baseUrl`. The model list is managed via `models` verbs; config-set for any other key rejects with a friendly error.

## Checking credential health

The credential-health store is fed by two paths:
- **Accept-time validation** — the wizard's "Validate credential" button writes the full outcome (success flips `Valid`, 401 flips `Invalid`).
- **Use-time watchdog** — HTTP middleware on the runtime's outbound clients watches for 401/403 responses and updates the row (`401→Invalid`, `403→Revoked`). Other statuses don't flap the row.

```
$ spring agent-runtime credentials status claude
claude / default → Valid (last checked 2026-04-20 09:03:12Z)
```

Or for an unhealthy credential:

```
$ spring agent-runtime credentials status openai
openai / default → Revoked (last checked 2026-04-20 10:45:02Z)
  reason: Forbidden
```

A 404 means no validation has been recorded yet — run the wizard's validate button or, for runtimes with multi-credential setups, use `--secret-name <name>`.

## Verifying the container baseline

Some runtimes need host-side tooling (e.g. the `claude` CLI on PATH). The runtime publishes its checklist via `IAgentRuntime.VerifyContainerBaselineAsync`; the CLI surfaces it:

```
$ spring agent-runtime verify-baseline claude
Runtime 'claude' baseline: OK
```

Failures print a bullet list of human-readable errors and exit 1:

```
$ spring agent-runtime verify-baseline claude
Runtime 'claude' baseline: FAILED
  - 'claude' CLI was not found on PATH (expected for ToolKind=claude-code-cli)
```

Runtimes that need no host-side tooling (the OpenAI-compatible set) pass trivially.

## Uninstalling a runtime

```
$ spring agent-runtime uninstall claude
Uninstall runtime 'claude' from the current tenant? [y/N]: y
Uninstalled runtime 'claude'.
```

Add `--force` to skip the prompt in scripts. Uninstall is soft-delete: re-installing revives the row and resets `InstalledAt`.

## Troubleshooting

- **`validate-credential` returns `NetworkError`.** The runtime could not reach its backing service. `credentials status` will show the previous value (or `Unknown`) — the watchdog does not flap the row on transport failures. Check the container's outbound connectivity and rerun the wizard's validate button.
- **`verify-baseline` reports a missing binary.** The runtime expects a host tool that isn't in this container image. Rebuild with the tool installed, or switch to a runtime that uses `dapr-agent` (no host binary required).
- **`credentials status` returns 404.** No validation row has been recorded for this (runtime, secret). Run the wizard's validate button once to prime the row, or if you're calling the HTTP API directly, hit `POST /api/v1/agent-runtimes/{id}/validate-credential`.
- **`install` silently "succeeds" but `list` doesn't show the runtime.** Confirm the runtime package is registered in `src/Cvoya.Spring.Host.Api/Program.cs` (`AddCvoyaSpringAgentRuntime<Name>()` call); install writes to the current tenant only.
- **A model you pinned is missing from the wizard dropdown.** Re-check `models list <id>`. If the model is present in the list but absent in the wizard, check that the portal is refreshed (the wizard caches the model list per session).

## See also

- [Connector operator guide](connectors.md) — parallel guide for per-tenant connector installs.
- [Architecture: Agent Runtimes & Tenant Scoping](../architecture/agent-runtimes-and-tenant-scoping.md) — plugin model, install lifecycle, credential-health state machine.
- Tracker issue [#674](https://github.com/cvoya-com/spring-voyage/issues/674) — the refactor this surface ships with.
