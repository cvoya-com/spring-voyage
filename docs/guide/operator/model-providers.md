# Model Providers — Operator Guide

> Practical CLI workflows for installing, configuring, and maintaining model providers on a tenant. Audience: operators with some ops background but no prior Spring Voyage context.

A **model provider** is the company whose API hosts a set of LLMs — `anthropic`, `openai`, `google`, `ollama`. The provider is the platform's credential and routing boundary: one credential row per `(tenant, provider, authMethod)`, one live-catalogue lookup per provider, shared across every runtime that targets that provider.

## Providers vs runtimes

The platform distinguishes three related concepts:

- **AgentRuntime** — the in-container execution engine (Claude Code, Codex, Gemini CLI, Spring Voyage Agent). Picked at unit/agent create time.
- **ModelProvider** — the company hosting the LLMs. The credential / routing boundary.
- **Model** — a specific LLM, identified by the structured pair `{provider, id}`.

The operator's job in this guide is to install **providers**. Runtimes are not installed per-tenant — they are picked at unit/agent create time from the catalogue's closed list. There is no `spring agent-runtime …` verb family; install/configure/refresh-models all live under `spring model-provider …`.

**Where this fits.** On a fresh OSS deployment the Worker host's bootstrap installs every catalogued provider onto the default tenant automatically — so on day one every provider is already visible to the wizard. You only reach for `install` / `uninstall` when curating (e.g. hiding Ollama in a cloud-only deployment, or reshaping the model list for a specific provider).

All commands below assume you've authenticated the CLI (`spring auth`). Every mutation is **CLI-only** — the portal renders read-only views of this data, but writes come through `spring`.

## Listing installed providers

```
$ spring model-provider list
id          displayName  defaultModel        models
anthropic   Anthropic    claude-opus-4-7     claude-opus-4-7,claude-sonnet-4-6,claude-haiku-4-5-20251001
google      Google       gemini-2.0-flash    gemini-2.0-flash,gemini-1.5-pro,gemini-1.5-flash
ollama      Ollama       llama3.2:3b         llama3.2:3b,llama3.2:1b,qwen2.5:7b
openai      OpenAI       gpt-4o              gpt-4o,gpt-4o-mini,gpt-4-turbo
```

`list` reads tenant-installed rows; on a fresh deployment that's every catalogued provider. Pipe through `-o json` for script-friendly output. The host-registered superset (every entry in `runtime-catalog.yaml`) lives in source — for parity with the CLI list, hit `GET /api/v1/tenant/model-providers/installs` directly.

## Inspecting a provider install

```
$ spring model-provider show anthropic
id             anthropic
displayName    Anthropic
defaultModel   claude-opus-4-7
models         claude-opus-4-7,claude-sonnet-4-6,claude-haiku-4-5-20251001
installedAt    2026-04-20T05:30:12Z
updatedAt      2026-04-20T05:30:12Z
```

A 404 means the provider is not installed on the current tenant — re-install with `spring model-provider install anthropic`.

## Installing or refreshing a provider

```
$ spring model-provider install anthropic
```

Install is idempotent: re-running with no flags is a no-op against operator-edited config. Flags override:

```
$ spring model-provider install openai \
    --model gpt-4o \
    --model gpt-4o-mini \
    --default-model gpt-4o \
    --base-url https://openai-proxy.example.com
```

- `--model <id>` — repeatable. Pins the tenant's configured list (replaces what was there).
- `--default-model <id>` — pre-selected in the wizard.
- `--base-url <url>` — for Ollama or OpenAI-compatible gateways.

**Unknown provider id** → `spring` exits 1 with: `Model provider '<id>' is not registered with the host.` Valid ids are exactly the entries in the platform's provider catalog — the closed v0.1 set is `anthropic`, `openai`, `google`, `ollama`.

## Validating a credential

When you want to confirm a credential resolves under the `(tenant, provider, authMethod)` key — without the side-effect of refreshing the model catalogue — use `validate-credential`:

```
$ spring model-provider validate-credential anthropic --credential sk-ant-api-…
Credential for provider 'anthropic' is valid (validated at 2026-04-22 10:15:00Z).

$ spring model-provider validate-credential ollama
Provider 'ollama' does not require credentials.
```

Behaviour:

- Probes the provider's backing service exactly like `refresh-models` does, but **does not** touch the tenant's stored model list — the catalog is only rotated by `refresh-models`.
- Records the outcome in the credential-health store on `Valid` / `Invalid` outcomes; transient `NetworkError` results don't flip a previously `Valid` row (mirrors the use-time HTTP watchdog).
- Honours `--output json` and `--secret-name <name>` for multi-credential layouts.
- Exit codes: `0` on a `Valid` outcome; `1` on `Invalid`, `Unknown`, or transport errors. Scripts can branch on the exit code.

For providers with an empty `authMethods` list (Ollama in v0.1), `validate-credential` exits with a friendly "does not require credentials" message rather than probing.

## Refreshing the model catalogue from the provider

When you want the tenant's list to match whatever the provider currently publishes (rather than curating it by hand), use `refresh-models`. The CLI hits the provider's `/v1/models` endpoint (or equivalent — `/api/tags` for Ollama) and replaces the stored list with the returned ids.

```
$ spring model-provider refresh-models openai     --credential sk-proj-…
$ spring model-provider refresh-models anthropic  --credential sk-ant-api-…
$ spring model-provider refresh-models google     --credential AIza…
$ spring model-provider refresh-models ollama                      # no credential needed
```

Behaviour:

- `DefaultModel` is preserved if it's still in the refreshed list; otherwise the endpoint resets it to the first live entry so the tenant never keeps a dangling default.
- `BaseUrl` is untouched — refresh is only about the catalogue.
- Units with a pinned model id that the provider no longer publishes are **not** rewritten — the pinned id flows through to the next run and surfaces as a unit-level error, not a silent catalogue change.

Failure modes (each exits 1, leaves the stored list untouched):

- **Not installed** (404) — run `spring model-provider install <id>` first.
- **Credential rejected** (401) — supply `--credential` with a live key.
- **Live catalogue not supported** (502) — some credential formats (e.g. Claude.ai OAuth tokens against the Anthropic Platform) or unreachable endpoints (e.g. offline Ollama) cannot enumerate models. The seed catalogue from `runtime-catalog.yaml` remains authoritative in that case.

## Setting non-model config

```
$ spring model-provider config set anthropic defaultModel=claude-opus-4-7
$ spring model-provider config set ollama baseUrl=http://ollama.internal:11434
$ spring model-provider config set ollama baseUrl=        # clears the field
```

Supported keys: `defaultModel`, `baseUrl`. The model list is managed via `install --model` / `refresh-models`; `config set` for any other key rejects with a friendly error.

To read back the config slot without the noisy `show` table, use the symmetric `config get`:

```
$ spring model-provider config get anthropic
id            anthropic
defaultModel  claude-opus-4-7
baseUrl       (none)
models        claude-opus-4-7,claude-sonnet-4-6,claude-haiku-4-5-20251001
```

`config get` honours `--output json` for scripting and returns 404 (exit 1) when the provider is not installed on the current tenant.

## Checking credential health

The credential-health store is fed by two paths:

- **`validate-credential`** writes the outcome on every probe (see above).
- **The use-time watchdog** — HTTP middleware on the provider's outbound clients watches for 401/403 responses and updates the row (`401 → Invalid`, `403 → Revoked`). Other statuses don't flap the row.

```
$ spring model-provider credentials status anthropic
anthropic / default → Valid (last checked 2026-04-20 09:03:12Z)
```

Or for an unhealthy credential:

```
$ spring model-provider credentials status openai
openai / default → Revoked (last checked 2026-04-20 10:45:02Z)
  reason: Forbidden
```

A 404 means no observation has landed yet — exercise the provider once (create a unit, run `spring unit revalidate <name>`, or run `spring model-provider validate-credential <id>` to prime the row directly).

For providers with multi-credential setups, use `--secret-name <name>`.

## Uninstalling a provider

```
$ spring model-provider uninstall anthropic
Uninstall provider 'anthropic' from the current tenant? [y/N]: y
Uninstalled provider 'anthropic'.
```

Add `--force` to skip the prompt in scripts. Uninstall is soft-delete: re-installing revives the row and resets `InstalledAt`.

**Effect on existing units.** Uninstalling a provider does **not** retroactively break units already pinned to that provider's models — their stored `model.provider` flows through unchanged. The next dispatch will fail with a precise credential-resolution error if the credential row is gone, or succeed if the credential is still resolvable. Re-install to restore the wizard surface; uninstall is about what's *visible* on tenant create-flows, not about disabling already-running units.

## Unit validation lifecycle

Credential / tool / model checks run inside the chosen container image when a unit enters validation. The operator-facing surface is the unit lifecycle and `spring unit revalidate` — there is no per-provider "validate this unit's credential" button.

A new unit walks through:

```
Draft → Validating → Stopped           (success — ready for `spring unit start`)
         │
         └──────── → Error              (any probe step failed)
```

`Validating` runs four ordered steps; the first failure short-circuits:

1. `PullingImage` — the dispatcher pulls the unit's configured image.
2. `VerifyingTool` — runs the runtime's tool-presence probe (e.g. `claude --version` / `curl --version`).
3. `ValidatingCredential` — runs the runtime's credential probe (e.g. `GET /v1/models` via `curl`) against the resolved `(tenant, provider, authMethod)` credential.
4. `ResolvingModel` — confirms the requested model id exists in the provider's catalogue.

Each step emits a `ValidationProgress` activity event (live in the portal's Validation panel and the CLI's progress stream). On failure the unit's `LastValidationError` carries a structured `{code, message, details}` for operators; the raw credential is never included in the error.

Retry after fixing the underlying issue with:

```
$ spring unit revalidate my-unit
```

Allowed only from `Error` or `Stopped`.

## Runtime-image contract (for unit images)

The in-container probe interpreters shell out to a small toolset; every image used as a unit runtime must include the runnable binary the probe needs. Failing to satisfy this surfaces cleanly as `ToolMissing` (exit 22) — never as a cryptic credential-validation failure.

| Runtime         | Provider(s) | Required binary | Why |
|-----------------|-------------|-----------------|-----|
| `claude-code`   | `anthropic` | `claude` | Credential and model probes invoke the Claude Code CLI. |
| `codex`         | `openai` | `curl` | Credential + model probes call the OpenAI API via `curl`. |
| `gemini`        | `google` | `curl` | Credential + model probes call the Google API via `curl`. |
| `spring-voyage` | all | `curl` | Credential + model probes call the active provider's endpoint via `curl`. |

The OSS images shipped by the default Worker deployment already satisfy the binary contract. Operators building custom images should keep the appropriate binary on `PATH` — `curl` is typically the smallest addition (an `apk add curl` or `apt-get install -y curl` step).

## Troubleshooting

- **Unit stays in `Validating` forever.** The validation workflow dispatched but the dispatcher sidecar is unhealthy. Check the worker / dispatcher logs and confirm the sidecar is healthy. `spring unit revalidate <name>` restarts the workflow cleanly once the underlying issue is fixed.
- **Unit is in `Error` with `LastValidationError.Code == "ToolMissing"`.** The image does not carry the binary the probe needs (`curl`, `claude`, etc.). Rebuild the image per the runtime-image contract above.
- **Unit is in `Error` with `LastValidationError.Code == "CredentialInvalid"`.** The provider rejected the credential (401 / 403). Update the secret (`spring secret …`) and run `spring unit revalidate <name>`. Confirm the credential resolves with `spring model-provider validate-credential <id>` first.
- **Unit is in `Error` with `LastValidationError.Code == "ModelNotFound"`.** The requested model id is not in the provider's live catalogue. Refresh the catalogue (`spring model-provider refresh-models <id>`) or switch the unit to a listed model via `spring unit execution set <name> --model <id>` + `spring unit revalidate`.
- **`credentials status` returns 404.** No observation has landed yet. Run `spring model-provider validate-credential <id> --credential <key>` to prime the row directly without rotating the catalogue, or exercise the provider via a unit dispatch.
- **`install` silently "succeeds" but `list` doesn't show the provider.** Confirm the provider id is in the platform's catalogue — the closed v0.1 set is `anthropic`, `openai`, `google`, `ollama`. Install writes to the current tenant only.
- **A model you pinned is missing from the wizard dropdown.** Re-check the configured list with `spring model-provider show <id>`. If the model is present in the list but absent in the wizard, refresh the portal session (the model list caches per session).

## See also

- [Connector operator guide](connectors.md) — parallel guide for per-tenant connector installs.
- [CLI reference](../../cli-reference.md) — the canonical command-by-command surface.
