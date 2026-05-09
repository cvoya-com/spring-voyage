# CLI Reference — `spring model-provider` and `spring connector`

> Reference for the CLI-only admin surfaces. The `spring` CLI ships many other verbs (`unit`, `agent`, `secret`, `boundary`, …) — this doc focuses on the two verb families dedicated to the tenant-install + credential-health layer. Every mutation below is CLI-only by design; the portal shows read-only views only.

All examples assume you've authenticated (`spring auth login`). Use `-o json` on any list / show verb for script-friendly output.

> **ADR-0038 reshape.** The `spring agent-runtime …` verb tree was deleted and re-keyed on **provider id** as `spring model-provider …`. Agent runtime (the launcher kind) and model provider (the credential-bearing service) are now distinct concepts — see [`docs/decisions/0038-agent-runtime-and-model-provider-split.md`](decisions/0038-agent-runtime-and-model-provider-split.md). The legacy `agent-runtime` verb is **not aliased** — typing it surfaces the parser's standard "unknown command" error.

## `spring model-provider`

Manage tenant-scoped model-provider installs (Anthropic, OpenAI, Google, Ollama, …). Provider id — not runtime id — is the routing key.

### `list`

```
$ spring model-provider list
```

Lists every model provider installed on the current tenant. The runtime catalogue (`claude-code`, `codex`, `gemini`, `spring-voyage`, `custom`) is a closed enum on the host and not part of this listing — provider installs are the operator-controlled axis.

### `show <id>`

```
$ spring model-provider show anthropic
```

Shows an installed provider's metadata and configured models. Exits 1 with a "not installed" message if no install row exists. Allowed ids today: `anthropic`, `openai`, `google`, `ollama` (consult the host's runtime-catalog for the live superset).

### `install <id> [--model m ...] [--default-model m] [--base-url url]`

```
# Seed defaults
$ spring model-provider install anthropic

# Pin a model list on install
$ spring model-provider install openai \
    --model gpt-4o --model gpt-4o-mini \
    --default-model gpt-4o

# Ollama via a custom host
$ spring model-provider install ollama --base-url http://ollama.internal:11434
```

Idempotent. Re-running with no flags preserves operator-edited config. Repeat `--model` for multiple entries; the first value becomes `--default-model` if that flag is absent.

### `uninstall <id> [--force]`

```
$ spring model-provider uninstall anthropic --force
```

Soft-deletes the install row. Without `--force`, the CLI prompts for `y/N` confirmation.

### `config get <id>` / `config set <id> <key=value>`

```
$ spring model-provider config get anthropic
id            anthropic
defaultModel  claude-opus-4-7
baseUrl       (none)
models        claude-opus-4-7,claude-sonnet-4-6

$ spring model-provider config set anthropic defaultModel=claude-opus-4-7
$ spring model-provider config set ollama       baseUrl=http://ollama.internal:11434
$ spring model-provider config set ollama       baseUrl=                # clears
$ spring model-provider config set anthropic    models=claude-opus-4-7,claude-sonnet-4-6
```

Supported keys: `defaultModel`, `baseUrl`, `models` (comma-separated). Any other key rejects with a clear message. `config get` is the lighter-weight read counterpart to `model-provider show` and renders only the configurable slot.

### `credentials status <id> [--secret-name name]`

```
$ spring model-provider credentials status anthropic
anthropic / default → Valid (last checked 2026-04-20 09:03:12Z)
```

Reads the shared credential-health store, which is fed by both the use-time watchdog and the `validate-credential` probe. A 404 means no row has been recorded yet — run `spring model-provider validate-credential <id> --credential <key>` (or use the portal's `/settings/model-providers` page) to prime it. Pass `--secret-name` for multi-credential providers.

### `validate-credential <id> [--credential <value>] [--secret-name <name>]`

```
$ spring model-provider validate-credential anthropic --credential sk-ant-api-...
$ spring model-provider validate-credential ollama                     # no credential needed
```

Probes the provider with the supplied credential and updates the credential-health row. **Does not** rotate the model catalogue — see `refresh-models` for that. Exits non-zero when the probe response carries `ok=false`, even if the HTTP call itself succeeded — script callers can tell "host unreachable" from "host reached, credential rejected".

### `refresh-models <id> [--credential <value>]`

```
# Provider-authenticated providers
$ spring model-provider refresh-models openai     --credential sk-proj-...
$ spring model-provider refresh-models anthropic  --credential sk-ant-api-...
$ spring model-provider refresh-models google     --credential AIza...

# Credential-less providers (local Ollama)
$ spring model-provider refresh-models ollama
```

Fetches the provider's live model catalogue from its backing service (typically `/v1/models` or equivalent) and replaces the tenant's configured model list with the returned entries. `defaultModel` is preserved if it is still in the refreshed list; otherwise it resets to the first live entry. `baseUrl` is never touched — refresh is about the catalogue, not the endpoint.

The command exits 1 when:

- The provider is not installed on the current tenant (404).
- The provider rejects the supplied credential (401).
- The provider cannot enumerate live models — e.g. an unreachable Ollama endpoint (502). The stored model list is left untouched in every failure case.

## `spring agent` and `spring unit` (execution-shorthand flags)

ADR-0038 reshapes the per-agent / per-unit execution shorthands into three flags:

| Flag | What it sets | Required when |
|------|--------------|---------------|
| `--runtime <id>` | `execution.runtime` (agent runtime kind, closed enum: `claude-code`, `codex`, `gemini`, `spring-voyage`, `custom`) | always when supplying inline credentials |
| `--model-provider <id>` | `execution.model.provider` (the structured-model provider half) | required for multi-provider runtimes (`spring-voyage`, `custom`); optional for fixed-provider runtimes — must match the implied provider when supplied |
| `--model <id>` | `execution.model.id` (the structured-model id half) | whenever you want to pin a specific model |

The container image shorthand remains: `--image <ref>`. The agent-only `--hosting <ephemeral|persistent>` flag also remains.

The legacy `--agent` and flat `--provider` flags are **rejected at parse time** with a clear migration hint — there is no compatibility alias.

The legacy `--container-runtime` flag is also **rejected at parse time** under ADR-0039 because container runtime is platform configuration.

### `spring unit create` / `spring unit execution set`

```
# Create a top-level unit pinned to a fixed-provider runtime
$ spring unit create my-unit --top-level --runtime claude-code --model claude-opus-4-7

# Pin a Spring Voyage Agent unit to OpenAI
$ spring unit create my-unit --top-level \
    --runtime spring-voyage \
    --model-provider openai \
    --model gpt-4o

# Update the unit's execution defaults later
$ spring unit execution set my-unit \
    --runtime claude-code --model claude-opus-4-7 \
    --image ghcr.io/example/claude:1
```

Inline credentials are still supplied via `--api-key` / `--api-key-from-file`, paired with `--runtime`. The CLI consults the runtime catalogue to pick the matching provider id and writes the secret on that provider's install row.

### `spring agent create` / `spring agent execution set`

```
# Create a scratch agent with a model override
$ spring agent create --name ada --unit eng --model claude-opus-4-7

# Install from a package with inputs and a connector binding
$ spring agent create --name ada --from-package software-engineering \
    --input github_owner=cvoya-com \
    --connector github=binding-123

# Override only the model id later — provider is preserved
$ spring agent execution set ada --model claude-sonnet-4-6

# Create an agent that inherits all execution config from its parent unit(s)
$ spring agent create --name ada --unit eng --inherit
```

> **Migration note.** The positional `<name>` argument was removed in v0.1. Use `--name <display-name>`.

`--unit <id>` is optional and repeatable. Omitting it creates a top-level tenant-parented agent; one or more `--unit` values create unit memberships. Use Guids in scripts.

Scratch-create flags:

| Flag | Effect |
|---|---|
| `--name <display-name>` | Required. Sets the agent display name; the platform assigns the stable agent id. |
| `--description <text>` | Sends the optional description on the create request. |
| `--role <role>` | Sends the optional agent role. |
| `--definition <json>` / `--definition-file <path>` | Sends an explicit agent definition JSON document. |
| `--inherit` | Sends no create-time execution shorthand fields; execution resolves from the parent unit set or tenant defaults. |
| `--image <ref>` | Sets `definitionJson.execution.image`. |
| `--runtime <id>` | Sets `definitionJson.execution.runtime`. |
| `--model-provider <id>` | Sets `definitionJson.execution.model.provider`. |
| `--model <id>` | Sets `definitionJson.execution.model.id`. |

Package-create flags:

| Flag | Effect |
|---|---|
| `--from-package <name>` | Starts package installation through `POST /api/v1/packages/install`. |
| `--input <key=value>` | Repeatable package input value. Only valid with `--from-package`. |
| `--connector <slug=binding-id>` | Repeatable connector binding override. Only valid with `--from-package`. |

Mutual exclusions are enforced at parse time: `--inherit` cannot be combined with execution shorthands, `--from-package` cannot be combined with `--definition` / `--definition-file` or execution shorthands, and `--input` / `--connector` require `--from-package`.

Per-field clear targets the new field-key surface: `image`, `runtime`, `model-provider`, `model`, `hosting` (agent only). Clearing `model-provider` wipes only the provider half of the structured `execution.model`; clearing `model` wipes the whole `{provider, id}` pair.

## `spring unit` (validation surface)

The `unit` verb family carries many subcommands (see `spring unit --help`); the two that interact directly with the backend validation flow are covered below.

### `create [--wait | --no-wait]`

```
$ spring unit create my-unit --top-level --runtime claude-code
$ spring unit execution set my-unit --image ghcr.io/example/claude:1 --no-wait
```

On success the CLI returns 201 and then **polls** the unit's terminal state. `--wait` is the **default**; the command blocks until the `UnitValidationWorkflow` finishes and exits with a validation-code-derived exit code (see the table below). `--no-wait` returns immediately after the 201, leaving the unit in `Validating`.

### `revalidate <name> [--wait | --no-wait]`

```
$ spring unit revalidate my-unit
$ spring unit revalidate my-unit --no-wait
```

Calls `POST /api/v1/units/{name}/revalidate`, which is allowed only from `Error` or `Stopped`. The handler flips the unit into `Validating` and dispatches a fresh `UnitValidationWorkflow` run; the CLI polls the same way `create` does.

Exits `2` (usage error) when the unit is not in an allowed state — the server returns 409 with the current status in the problem-details `extensions.currentStatus`.

### Validation exit codes

Shared by `spring unit create` and `spring unit revalidate` (stable, additive-only):

| Exit | `UnitValidationCodes` | Meaning |
|------|-----------------------|---------|
| 0  | — | Success (terminal passing state) |
| 1  | — | Unknown / transport error |
| 2  | — | Usage error or illegal state for the op |
| 20 | `ImagePullFailed` | Image could not be pulled |
| 21 | `ImageStartFailed` | Image pulled but refused to start |
| 22 | `ToolMissing` | Required binary absent from the image (see the runtime-image contract) |
| 23 | `CredentialInvalid` | Backend rejected the credential (401/403) |
| 24 | `CredentialFormatRejected` | Credential shape rejected before the network call |
| 25 | `ModelNotFound` | Provider does not publish the requested model id |
| 26 | `ProbeTimeout` | Step exceeded its timeout |
| 27 | `ProbeInternalError` | Probe interpreter crashed on the output |

Operators script against these numbers — the contract is additive-only (no renumbering).

## `spring connector`

Manage tenant-scoped connector installs (alongside the existing per-unit binding verbs).

### `list`

```
$ spring connector list
```

Tenant-installed connectors. `spring connector catalog` returns the same install-scoped list — both verbs render exactly what the portal shows in its connector chooser. Connector types registered with the host but **not** installed on the current tenant are intentionally invisible from both surfaces; inspect the DI registry directly if you need that superset.

### `show <slugOrId>`

```
$ spring connector show github
```

Shows install metadata for a connector on the current tenant. Exits 1 with a "not installed" message when absent.

### `install <slugOrId>`

```
$ spring connector install github
```

Idempotent. No config flags — connector-specific tenant config evolves alongside each connector's typed schema; today OSS connectors carry no tenant-level config.

### `uninstall <slugOrId> [--force]`

```
$ spring connector uninstall github --force
```

Soft-deletes. Uninstalling a connector does **not** retroactively break units already bound through it; new bindings are rejected. Use `spring connector bindings <slug>` to enumerate affected units first.

### `credentials status <slugOrId> [--secret-name name]`

```
$ spring connector credentials status github
github / default → Valid (last checked 2026-04-20 09:03:12Z)

$ spring connector credentials status github --secret-name github-app-private-key
github / github-app-private-key → Invalid (last checked 2026-04-20 10:45:02Z)
  reason: Unauthorized
```

Reads the shared credential-health store. For connectors without auth (Arxiv, WebSearch), the row stays `Unknown` — these connectors surface a "does not require credentials" message via `POST /validate-credential`.

### Per-unit binding verbs (recap)

Orthogonal to the tenant-install surface:

- `spring connector unit-binding --unit <name>` — show a unit's active binding.
- `spring connector bind --unit <name> --type <slug> ...` — bind a unit (and set typed config).
- `spring connector bindings <slug>` — units bound to a connector type.

These predate the tenant-install surface and work for units whose tenant has the connector installed. (`spring connector catalog`, despite its historical name, also lives here only as a tenant-install listing — see `list` above.)

## Top scenarios

1. **Fresh tenant, Anthropic auth check.** `spring model-provider credentials status anthropic` → if 404, prime via `spring model-provider validate-credential anthropic --credential sk-ant-...`.
2. **Pin a model list on install.** `spring model-provider install anthropic --model claude-opus-4-7 --model claude-sonnet-4-6`.
3. **Reconcile the tenant's list with what the provider currently publishes.** `spring model-provider refresh-models openai --credential sk-proj-…`.
4. **Retire a model from the catalogue.** `spring model-provider config set openai models=gpt-4o` (existing units keep their pinned id per the pass-through rule).
5. **Re-run backend validation on a failed unit.** `spring unit revalidate my-unit` — dispatches a fresh `UnitValidationWorkflow` run; exits 20–27 map onto the underlying `UnitValidationCodes`.
6. **Install Ollama with a custom node URL.** `spring model-provider install ollama --base-url http://ollama.internal:11434`.
7. **Hide OpenAI from a tenant.** `spring model-provider uninstall openai --force`.
8. **Re-enable OpenAI later.** `spring model-provider install openai` — install is upsert-shaped; prior config is preserved where possible.
9. **Author a Spring Voyage Agent unit pinned to a specific provider.** `spring unit create my-unit --top-level --runtime spring-voyage --model-provider openai --model gpt-4o`.
10. **Override only the model id on an existing agent.** `spring agent execution set ada --model claude-sonnet-4-6`.
11. **Install GitHub connector on a tenant that didn't auto-seed it.** `spring connector install github`.
12. **Audit GitHub credential state.** `spring connector credentials status github --secret-name github-app-private-key`.
13. **See which units would break if we uninstall GitHub.** `spring connector bindings github`.

## See also

- [ADR-0038: Agent Runtime and Model Provider split](decisions/0038-agent-runtime-and-model-provider-split.md) — the architectural split this surface implements.
- [Model Providers operator guide](guide/operator/model-providers.md) — prose walkthroughs for every `spring model-provider …` verb.
- [Connectors operator guide](guide/operator/connectors.md) — prose walkthroughs for connector verbs.
