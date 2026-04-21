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

## Unit validation lifecycle

> **Backend-side validation — [#941](https://github.com/cvoya-com/spring-voyage/issues/941) landed in V2.** The accept-time host-side probe was removed in #941. Credential / tool / model checks now run inside the chosen container image via `UnitValidationWorkflow`, a Dapr Workflow dispatched when a unit enters `Validating`. The operator-facing surface is the unit lifecycle and `spring unit revalidate` — not a per-runtime "validate credential" button.

A new unit walks through:

```
Draft → Validating → Stopped           (success — ready for `spring unit start`)
         │
         └──────── → Error              (any probe step failed)
```

`Validating` runs four ordered steps; the first failure short-circuits:

1. `PullingImage` — the dispatcher pulls the unit's configured image.
2. `VerifyingTool` — runs the runtime's tool-presence probe (e.g. `claude --version` / `curl --version`).
3. `ValidatingCredential` — runs the runtime's credential probe (e.g. `GET /v1/models` via `curl`).
4. `ResolvingModel` — confirms the requested model id exists in the provider's catalog.

Each step emits a `ValidationProgress` activity event (live in the portal's Validation panel and the CLI's progress stream). On failure the unit's `LastValidationError` carries a structured `{code, message, details}` for operators; the raw credential is never included in the error.

Retry after fixing the underlying issue with:

```
$ spring unit revalidate my-unit
```

Allowed only from `Error` or `Stopped`. See [`cli-reference.md` → Validation exit codes](../../cli-reference.md#validation-exit-codes) for the exit-code table (20 – 27 map to `UnitValidationCodes`).

## Runtime-image contract

The in-container probe interpreters shell out to a small toolset; every image used as a unit runtime must include the runnable binary the probe needs. Failing to satisfy this surfaces cleanly as `ToolMissing` (exit 22) — never as a cryptic credential-validation failure.

| Runtime | Required binary in the image | Why |
|---------|------------------------------|-----|
| `claude` | `claude` | Credential and model probes invoke the Claude Code CLI directly. |
| `openai` | `curl` | Credential + model probes call `api.openai.com` via `curl`. |
| `google` | `curl` | Credential + model probes call `generativelanguage.googleapis.com` via `curl`. |
| `ollama` | `curl` | Reachability + model probes call the configured Ollama URL via `curl`. |

The OSS runtime images shipped by the default Worker deployment already satisfy this contract. Operators building custom images should keep the appropriate binary on `PATH` — `curl` is typically the smallest addition (an `apk add curl` or `apt-get install -y curl` step).

## Checking credential health

The credential-health store is now fed by a single path — the **use-time watchdog**. HTTP middleware on the runtime's outbound clients watches for 401/403 responses and updates the row (`401 → Invalid`, `403 → Revoked`). Other statuses don't flap the row. The accept-time host-side endpoint the wizard used to drive was removed in #941; credential checks for a specific unit now run inside its container via `UnitValidationWorkflow` (see above).

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

A 404 means no watchdog observation has landed yet — exercise the runtime once (create a unit, or run `spring unit revalidate <name>`) to prime the row. For runtimes with multi-credential setups, use `--secret-name <name>`.

## Refreshing the model catalog from the provider

When an operator wants the tenant's list to match whatever the provider currently publishes (rather than curating it by hand), use `refresh-models`. The CLI hits the provider's `/v1/models` endpoint (or equivalent) and replaces the stored list with the returned ids.

```
$ spring agent-runtime refresh-models openai --credential sk-proj-…
$ spring agent-runtime refresh-models claude  --credential sk-ant-api-…
$ spring agent-runtime refresh-models google  --credential AIza…
$ spring agent-runtime refresh-models ollama                    # no credential needed
```

Behaviour:

- `DefaultModel` is preserved if it's still in the refreshed list; otherwise the endpoint resets it to the first live entry so the tenant never keeps a dangling default.
- `BaseUrl` is untouched — refresh is only about the catalog.
- Units with a pinned model id that the provider no longer publishes are **not** rewritten — the pinned id flows through to the next run and surfaces as a unit-level error, not a silent catalog change. Pinning reconciliation is tracked separately through `ExpertiseSkillRegistry` drift handling.

Failure modes (each exits 1, leaves the stored list untouched):

- **Not installed** (404) — run `spring agent-runtime install <id>` first.
- **Credential rejected** (401) — supply `--credential` with a live key.
- **Live catalog not supported** (502) — some credential formats (e.g. Claude.ai OAuth tokens against the Anthropic Platform) or unreachable endpoints (e.g. offline Ollama) cannot enumerate models. The seed catalog remains authoritative in that case.

## Uninstalling a runtime

```
$ spring agent-runtime uninstall claude
Uninstall runtime 'claude' from the current tenant? [y/N]: y
Uninstalled runtime 'claude'.
```

Add `--force` to skip the prompt in scripts. Uninstall is soft-delete: re-installing revives the row and resets `InstalledAt`.

## Troubleshooting

- **Unit stays in `Validating` forever.** The `UnitValidationWorkflow` dispatched but the dispatcher sidecar is unhealthy. Check the worker / dispatcher logs and confirm the Dapr sidecar responds on `/healthz`. `spring unit revalidate <name>` restarts the workflow cleanly once the underlying issue is fixed.
- **Unit is in `Error` with `LastValidationError.Code == "ToolMissing"`.** The image does not carry the binary the probe needs (`curl`, `claude`, etc.). Rebuild the image per the runtime-image contract above.
- **Unit is in `Error` with `LastValidationError.Code == "CredentialInvalid"`.** The provider rejected the credential (401 / 403). Update the secret (`spring secret …`) and run `spring unit revalidate <name>`.
- **Unit is in `Error` with `LastValidationError.Code == "ModelNotFound"`.** The requested model id is not in the provider's live catalog. Refresh the catalog (`spring agent-runtime refresh-models <id>`) or switch the unit to a listed model via `spring unit patch <name> --model <id>` + `spring unit revalidate`.
- **`credentials status` returns 404.** No watchdog observation has landed yet. Exercise the runtime (run a unit or `spring unit revalidate <name>`) to prime the row.
- **`install` silently "succeeds" but `list` doesn't show the runtime.** Confirm the runtime package is registered in `src/Cvoya.Spring.Host.Api/Program.cs` (`AddCvoyaSpringAgentRuntime<Name>()` call); install writes to the current tenant only.
- **A model you pinned is missing from the wizard dropdown.** Re-check `models list <id>`. If the model is present in the list but absent in the wizard, check that the portal is refreshed (the wizard caches the model list per session).

## See also

- [Connector operator guide](connectors.md) — parallel guide for per-tenant connector installs.
- [Architecture: Agent Runtimes & Tenant Scoping](../../architecture/agent-runtimes-and-tenant-scoping.md) — plugin model, install lifecycle, credential-health state machine.
- Tracker issue [#674](https://github.com/cvoya-com/spring-voyage/issues/674) — the refactor this surface ships with.
