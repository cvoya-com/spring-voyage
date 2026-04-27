# Connectors — Operator Guide

> Practical CLI workflows for installing, configuring, and maintaining connectors on a tenant. Audience: operators with some ops background but no prior Spring Voyage context.

Connectors are the plugin layer that bridges Spring Voyage units to external systems (GitHub, Arxiv, WebSearch, …). Each connector registers in the host at startup but becomes _visible_ to a tenant only after an install row exists. A connector's **typed per-unit config** (repository name, organisation, API base URL) is wired via `spring connector bind`; this guide focuses on the **tenant-level install surface** — who can use the connector at all.

On a fresh OSS deployment the Worker host's bootstrap installs every registered connector onto the default tenant automatically, so you usually skip straight to the "validate credentials" step. Reach for `install` / `uninstall` only when you want to curate the list (e.g. hide GitHub on a deployment that doesn't authorise outbound webhooks).

All commands below assume you've authenticated the CLI (`spring auth login`). Every mutation is **CLI-only** — the portal may render read-only banners but writes come through `spring`.

## Listing installed connectors

```
$ spring connector list
slug        name           installedAt            updatedAt
arxiv       arXiv          2026-04-20T05:30:12Z   2026-04-20T05:30:12Z
github      GitHub         2026-04-20T05:30:12Z   2026-04-20T05:30:12Z
websearch   Web Search     2026-04-20T05:30:12Z   2026-04-20T05:30:12Z
```

`list` reads tenant-installed rows; on a fresh deployment that's every registered connector. `spring connector catalog` returns the same install-scoped list — both verbs render exactly what the portal's connector chooser shows. Connector types registered with the host but **not** installed on the current tenant are intentionally invisible from both surfaces.

Pipe through `-o json` for script-friendly output.

## Inspecting an installed connector

```
$ spring connector show github
slug     name     installedAt            updatedAt
github   GitHub   2026-04-20T05:30:12Z   2026-04-20T05:30:12Z
```

A 404 means the connector is not installed on the current tenant — re-install with `spring connector install github`.

## Installing a connector

```
$ spring connector install github
```

Install is idempotent. The CLI does not take typed config flags — each connector's tenant-level config shape is its own concern, and the current OSS connectors either carry no tenant-level config or manage it per-unit. When a connector ships typed tenant-config keys, `spring connector config set` gains support for them (tracked under #689's follow-ups).

Per-unit config — the GitHub repo, organisation, webhook events — is set via `spring connector bind --unit <name> --type github ...`, **not** via this tenant-install verb.

**Unknown slug** → `spring` exits 1 with: `Connector '<slug>' is not registered.` Valid slugs match projects under `src/Cvoya.Spring.Connector.*` in the host. `spring connector catalog` only lists what's already installed on the tenant, so for the registered superset inspect the source tree (or hit the host's DI registry directly while debugging).

## Checking credential health

> **Connector credential validation — scope note for [#941](https://github.com/cvoya-com/spring-voyage/issues/941).** The in-container validation rework. retired the host-side probe for **agent runtimes** only; connector `POST /validate-credential` still runs on the API host (connectors don't yet have a container-image contract). The use-time watchdog described below remains the durable source of `Invalid` / `Revoked` signals for connectors. Rework tracked separately if/when connector probes move in-container.

The credential-health store feeds two paths:
- **Accept-time validation** — hitting `POST /api/v1/connectors/{slug}/validate-credential` writes the outcome. Subject to the rework banner above.
- **Use-time watchdog** — HTTP middleware on the connector's outbound clients watches for 401/403 responses and updates the row (`401→Invalid`, `403→Revoked`). Other statuses don't flap the row.

```
$ spring connector credentials status github
github / default → Valid (last checked 2026-04-20 09:03:12Z)
```

For connectors that don't carry auth (Arxiv, WebSearch), the row is `Unknown` and stays there — these connectors surface a friendly "does not require credentials" message via the HTTP validate endpoint.

Multi-credential connectors (e.g. GitHub App id + private key) store one row per credential; use `--secret-name <name>` to probe a specific one:

```
$ spring connector credentials status github --secret-name github-app-private-key
```

## Per-unit binding (recap)

Per-unit config is orthogonal to tenant installs. A unit can only be bound to a connector that is installed on its tenant. The binding verbs live on the same `spring connector` root for convenience:

- `spring connector unit-binding --unit <name>` — show the unit's active binding + typed config.
- `spring connector bind --unit <name> --type <slug> ...` — bind (writes typed config).
- `spring connector bindings <slug>` — list units bound to a given connector type.

These predate the tenant-install surface; they're part of the per-unit binding flow documented in the unit-creation guide. They only work for units whose tenant has the connector installed.

## Uninstalling a connector

```
$ spring connector uninstall github
Uninstall connector 'github' from the current tenant? [y/N]: y
Uninstalled connector 'github'.
```

Add `--force` to skip the prompt in scripts. Uninstall is soft-delete: re-installing revives the row and resets `InstalledAt`.

**Impact on bound units.** Uninstalling a connector from a tenant does **not** retroactively break units already bound through it — the existing per-unit binding rows stay in place. New bindings to the uninstalled connector will be rejected. Use `spring connector bindings <slug>` to enumerate affected units before uninstall.

## Troubleshooting

- **`credentials status` returns 404.** No validation row has been recorded for this (connector, secret). For connectors with auth, run the wizard's validate button once to prime the row, or hit `POST /api/v1/connectors/{slug}/validate-credential` directly. For connectors without auth, this state is expected.
- **Validate-credential returns `Unknown` with "does not require credentials".** The connector's `ValidateCredentialAsync` returns `null` by default. Arxiv and WebSearch are always in this state.
- **`install` silently "succeeds" but `list` doesn't show the connector.** Confirm the connector package is registered in `src/Cvoya.Spring.Host.Api/Program.cs` (`AddCvoyaSpringConnector<Name>()` call); install writes to the current tenant only.
- **A unit fails to start after the connector was uninstalled.** The unit's per-unit binding row still references the connector; re-install the connector on the tenant, or unbind the unit via `spring connector unit-binding --unit <name>` → the DELETE path clears the binding.

## See also

- [Agent Runtimes operator guide](agent-runtimes.md) — parallel guide for per-tenant agent-runtime installs.
- [Architecture: Agent Runtimes & Tenant Scoping](../../architecture/agent-runtimes-and-tenant-scoping.md) — plugin model, credential-health state machine.
- [Per-unit connector binding](../user/units-and-agents.md) — wiring a unit to an installed connector.
