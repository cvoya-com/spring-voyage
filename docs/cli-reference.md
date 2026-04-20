# CLI Reference — `spring agent-runtime` and `spring connector`

> Reference for the CLI-only admin surfaces introduced by the #674 refactor. The `spring` CLI ships many other verbs (`unit`, `agent`, `secret`, `boundary`, …) — this doc focuses on the two verb families dedicated to the tenant-install + credential-health layer. Every mutation below is CLI-only by design; the portal shows read-only views only.

All examples assume you've authenticated (`spring auth login`). Use `-o json` on any list / show verb for script-friendly output.

## `spring agent-runtime`

Manage tenant-scoped agent runtime installs.

### `list`

```
$ spring agent-runtime list
```

Lists every agent runtime installed on the current tenant. Distinct from "everything the host can serve" — use the host's registered package list if you need the superset.

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
$ spring agent-runtime models set claude claude-sonnet-4-5,claude-opus-4-1
$ spring agent-runtime models add claude claude-opus-4-1
$ spring agent-runtime models remove claude claude-opus-4-1
```

Sugar over `PATCH /config` that reshapes the tenant's model list. `add` is a no-op if the id is already present (case-insensitive).

### `config set <id> <key=value>`

```
$ spring agent-runtime config set claude defaultModel=claude-sonnet-4-5
$ spring agent-runtime config set ollama baseUrl=http://ollama.internal:11434
$ spring agent-runtime config set ollama baseUrl=        # clears
```

Supported keys: `defaultModel`, `baseUrl`. Any other key rejects with a clear message pointing at the `models` verb tree for model-list changes.

### `credentials status <id> [--secret-name name]`

```
$ spring agent-runtime credentials status claude
claude / default → Valid (last checked 2026-04-20 09:03:12Z)
```

Reads the shared credential-health store. 404 means no validation has been recorded — run the wizard's validate button or hit `POST /api/v1/agent-runtimes/{id}/validate-credential` directly to prime the row. Pass `--secret-name` for multi-credential runtimes.

### `verify-baseline <id>`

```
$ spring agent-runtime verify-baseline claude
Runtime 'claude' baseline: OK
```

Invokes the runtime's `VerifyContainerBaselineAsync`. Failures print one error per line and exit 1. Runtimes with no host-side tooling pass trivially.

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

## `spring connector`

Manage tenant-scoped connector installs (alongside the existing per-unit binding verbs).

### `list`

```
$ spring connector list
```

Tenant-installed connectors. For the registry superset (installed or not), use `spring connector catalog`.

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

- `spring connector catalog` — every connector type known to the host.
- `spring connector unit-binding --unit <name>` — show a unit's active binding.
- `spring connector bind --unit <name> --type <slug> ...` — bind a unit (and set typed config).
- `spring connector bindings <slug>` — units bound to a connector type.

These predate the tenant-install surface and work for units whose tenant has the connector installed.

## Top scenarios

1. **Fresh tenant, Claude auth check.** `spring agent-runtime credentials status claude` → primes via the wizard if 404.
2. **Add a new Claude model to the tenant.** `spring agent-runtime models add claude claude-opus-4-2`.
3. **Reconcile the tenant's list with what the provider currently publishes.** `spring agent-runtime refresh-models openai --credential sk-proj-…` (closes #720 — replaces the refresh-script).
4. **Retire a model from the catalog.** `spring agent-runtime models remove openai gpt-4o-mini` (existing units keep their pinned id per #674's pass-through rule).
5. **Verify Claude CLI is on PATH in this image.** `spring agent-runtime verify-baseline claude`.
6. **Install Ollama with a custom node URL.** `spring agent-runtime install ollama --base-url http://ollama.internal:11434`.
7. **Hide OpenAI from a tenant.** `spring agent-runtime uninstall openai --force`.
8. **Re-enable OpenAI later.** `spring agent-runtime install openai` — install is upsert-shaped; prior config is preserved where possible.
9. **Install GitHub connector on a tenant that didn't auto-seed it.** `spring connector install github`.
10. **Audit GitHub credential state.** `spring connector credentials status github --secret-name github-app-private-key`.
11. **See which units would break if we uninstall GitHub.** `spring connector bindings github`.

## See also

- [Agent Runtimes operator guide](user-guide/agent-runtimes.md) — prose walkthroughs for every verb.
- [Connectors operator guide](user-guide/connectors.md) — prose walkthroughs for connector verbs.
- [Architecture: Agent Runtimes & Tenant Scoping](architecture/agent-runtimes-and-tenant-scoping.md) — the plugin model these verbs manipulate.
