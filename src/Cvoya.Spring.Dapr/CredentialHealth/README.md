# Credential-health watchdog (`Cvoya.Spring.Dapr.CredentialHealth`)

Persistent store + HTTP watchdog that tracks whether an agent runtime's or connector's stored credential is currently healthy. Fed by two writers and consumed by a handful of readers:

## Writers

- **Accept-time validation** — the `POST /api/v1/agent-runtimes/{id}/validate-credential` and `POST /api/v1/connectors/{slugOrId}/validate-credential` endpoints call the subject's `ValidateCredentialAsync` and mirror the outcome via `ICredentialHealthStore.RecordAsync`.
- **Use-time watchdog** — `CredentialHealthWatchdogHandler` is a `DelegatingHandler` attached to named `HttpClient`s via `AddCredentialHealthWatchdog` (see `HttpClientBuilderExtensions`). Each response flows through the handler, which inspects the status code and flips the row on auth failures.

## Status-code mapping

| HTTP status | Persistent status |
|---|---|
| `200`–`299` | *(no change)* — the watchdog does not positively flip to `Valid` on success because a single healthy response is not a definitive re-validation. The accept-time flow is the source of `Valid`. |
| `401 Unauthorized` | `Invalid` |
| `403 Forbidden` | `Revoked` |
| everything else (5xx, 429, network errors) | *(no change)* — one upstream outage must not flap operator-facing status. |

The mapping is deliberately narrow. Services that need finer signalling (e.g. an OAuth provider returning `invalid_grant` with `token_expired`) should bypass the watchdog and call `RecordAsync` with `CredentialHealthStatus.Expired` directly.

## Tenant scope

All writes and reads resolve the ambient `ITenantContext.CurrentTenantId` — callers do not pass a tenant id. The watchdog handler opens a child DI scope per write so it can run safely inside background hosted services that have no request scope.

## Idempotency and state lifecycle

- `RecordAsync` is an upsert on the composite PK `(tenant_id, kind, subject_id, secret_name)`.
- Rows are NOT soft-deleted — the table carries current operational state, not business data. Uninstalling the subject (via the agent-runtime / connector install service) should remove its credential-health rows too; this wiring is currently indirect and will be tightened when the install uninstall path gains the hook.

## When to wire the watchdog

Every OSS runtime/connector that authenticates via `HttpClient` SHOULD wire the handler (see `CONVENTIONS.md` § 15). This PR ships the mechanism and a handful of exemplar wirings; remaining wire-ups across every runtime and connector package are tracked as follow-ups under the #674 refactor.
