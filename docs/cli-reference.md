# CLI Reference — `spring agent-runtime` and `spring connector`

> Reference for the CLI-only admin surfaces introduced by the #674 refactor. The `spring` CLI ships many other verbs (`unit`, `agent`, `secret`, `boundary`, …) — this doc focuses on the two verb families dedicated to the tenant-install + credential-health layer. Every mutation below is CLI-only by design; the portal shows read-only views only.

All examples assume you've authenticated (`spring auth login`). Use `-o json` on any list / show verb for script-friendly output.

## `spring agent-runtime`

Manage tenant-scoped agent runtime installs.

### `list`

```
$ spring agent-runtime list
```

Lists every agent runtime installed on the current tenant. Distinct from "everything the host can serve" — to inspect the registered superset, hit `GET /api/v1/agent-runtimes` directly (or query the DI registry from a debug session).

### `show <id>`

```
$ spring agent-runtime show claude
```

Shows an installed runtime's metadata and configured models. Exits 1 with a "not installed" message if no install row exists.

### `install <id> [--model m ...] [--default-model m] [--base-url url]`

```
# Seed defaults
$ spring agent-runtime install claude

# Pin a model list on install
$ spring agent-runtime install openai \
    --model gpt-4o --model gpt-4o-mini \
    --default-model gpt-4o

# Ollama via a custom host
$ spring agent-runtime install ollama --base-url http://ollama.internal:11434
```

Idempotent. Re-running with no flags preserves operator-edited config. Repeat `--model` for multiple entries.

### `uninstall <id> [--force]`

```
$ spring agent-runtime uninstall claude --force
```

Soft-deletes the install row. Without `--force`, the CLI prompts for `y/N` confirmation.

### `models list|set|add|remove`

```
$ spring agent-runtime models list claude
$ spring agent-runtime models set claude claude-opus-4-7,claude-sonnet-4-6,claude-haiku-4-5
$ spring agent-runtime models add claude claude-haiku-4-5
$ spring agent-runtime models remove claude claude-haiku-4-5
```

Sugar over `PATCH /config` that reshapes the tenant's model list. `add` is a no-op if the id is already present (case-insensitive).

### `config set <id> <key=value>`

```
$ spring agent-runtime config set claude defaultModel=claude-opus-4-7
$ spring agent-runtime config set ollama baseUrl=http://ollama.internal:11434
$ spring agent-runtime config set ollama baseUrl=        # clears
```

Supported keys: `defaultModel`, `baseUrl`. Any other key rejects with a clear message pointing at the `models` verb tree for model-list changes.

### `credentials status <id> [--secret-name name]`

```
$ spring agent-runtime credentials status claude
claude / default → Valid (last checked 2026-04-20 09:03:12Z)
```

Reads the shared credential-health store, which is now fed by the **use-time** watchdog only — the host-side accept-time `POST /validate-credential` endpoint was removed in #941. A 404 means no watchdog observation has landed yet; exercise the runtime once (create a unit, or run `spring unit revalidate <name>`) to prime the row. Pass `--secret-name` for multi-credential runtimes.

> **Backend-side validation — [#941](https://github.com/cvoya-com/spring-voyage/issues/941) landed in V2.** Accept-time probes now run inside the chosen container image via `UnitValidationWorkflow`, a Dapr Workflow dispatched when a unit enters `Validating`. Four steps (image pull → tool verify → credential validate → model resolve) run in order; the first failure short-circuits with a structured `UnitValidationError`. The CLI surfaces progress via `spring unit create` (default `--wait`) and exposes `spring unit revalidate <name>` for retries. Exit codes 20–27 map one-to-one onto `UnitValidationCodes`.

### `refresh-models <id> [--credential <value>]`

```
# Provider-authenticated runtimes (OpenAI / Claude / Google)
$ spring agent-runtime refresh-models openai --credential sk-proj-...
$ spring agent-runtime refresh-models claude --credential sk-ant-api-...
$ spring agent-runtime refresh-models google --credential AIza...

# Credential-less runtimes (local Ollama)
$ spring agent-runtime refresh-models ollama
```

Fetches the runtime's live model catalog from its backing service (typically `/v1/models` or equivalent) and replaces the tenant's configured model list with the returned entries. `DefaultModel` is preserved if it is still in the refreshed list; otherwise it resets to the first live entry. `BaseUrl` is never touched — refresh is about the catalog, not the endpoint.

The command exits 1 when:

- The runtime is not installed on the current tenant (404).
- The provider rejects the supplied credential (401).
- The runtime cannot enumerate live models — e.g. Claude.ai OAuth tokens against the Anthropic Platform REST surface, or an unreachable Ollama endpoint (502). The stored model list is left untouched in every failure case.

## `spring unit` (validation surface)

The `unit` verb family carries many subcommands (see `spring unit --help`); the two that interact directly with the backend validation flow are covered below.

### `create [--wait | --no-wait]`

```
$ spring unit create --name my-unit --image ghcr.io/example/claude:1 --tool claude-code-cli
$ spring unit create --from-template example/scratch --no-wait
```

On success the CLI returns 201 and then **polls** the unit's terminal state. `--wait` is the **default**; the command blocks until the `UnitValidationWorkflow` finishes and exits with a validation-code-derived exit code (see the table below). `--no-wait` returns immediately after the 201, leaving the unit in `Validating` — useful for scripts that kick off many units in parallel and reconcile later via `spring unit status`.

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

1. **Fresh tenant, Claude auth check.** `spring agent-runtime credentials status claude` → primes via the wizard if 404.
2. **Add a new Claude model to the tenant.** `spring agent-runtime models add claude claude-haiku-4-5`.
3. **Reconcile the tenant's list with what the provider currently publishes.** `spring agent-runtime refresh-models openai --credential sk-proj-…` (closes #720 — replaces the refresh-script).
4. **Retire a model from the catalog.** `spring agent-runtime models remove openai gpt-4o-mini` (existing units keep their pinned id per #674's pass-through rule).
5. **Re-run backend validation on a failed unit.** `spring unit revalidate my-unit` — dispatches a fresh `UnitValidationWorkflow` run; exits 20–27 map onto the underlying `UnitValidationCodes`.
6. **Install Ollama with a custom node URL.** `spring agent-runtime install ollama --base-url http://ollama.internal:11434`.
7. **Hide OpenAI from a tenant.** `spring agent-runtime uninstall openai --force`.
8. **Re-enable OpenAI later.** `spring agent-runtime install openai` — install is upsert-shaped; prior config is preserved where possible.
9. **Install GitHub connector on a tenant that didn't auto-seed it.** `spring connector install github`.
10. **Audit GitHub credential state.** `spring connector credentials status github --secret-name github-app-private-key`.
11. **See which units would break if we uninstall GitHub.** `spring connector bindings github`.

## See also

- [Agent Runtimes operator guide](guide/operator/agent-runtimes.md) — prose walkthroughs for every verb.
- [Connectors operator guide](guide/operator/connectors.md) — prose walkthroughs for connector verbs.
- [Architecture: Agent Runtimes & Tenant Scoping](architecture/agent-runtimes-and-tenant-scoping.md) — the plugin model these verbs manipulate.
