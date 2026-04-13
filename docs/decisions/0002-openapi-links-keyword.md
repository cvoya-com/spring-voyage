# 0002 — OpenAPI `links` keyword vs plain URL fields

- **Status:** Deferred — current approach (plain URL strings) stays; revisit when codegen support matures.
- **Date:** 2026-04-13
- **Closes:** [#195](https://github.com/savasp/spring-voyage/issues/195)
- **Related code:** `src/Cvoya.Spring.Host.Api/Models/ConnectorModels.cs`, `src/Cvoya.Spring.Host.Api/Endpoints/ConnectorEndpoints.cs`, `src/Cvoya.Spring.Host.Api/openapi.json`

## Context

The generic connector API (introduced with [#121](https://github.com/savasp/spring-voyage/issues/121) and [#125](https://github.com/savasp/spring-voyage/issues/125)) lets clients discover connector-owned endpoints at runtime. `ConnectorTypeResponse` and `UnitConnectorPointerResponse` carry the discovery pointers as plain strings:

- `configUrl` — template for the per-unit typed config endpoint (with a `{unitId}` placeholder)
- `actionsBaseUrl` — base URL under which the connector's typed actions are mounted
- `configSchemaUrl` — URL of the connector's JSON Schema config document

OpenAPI 3.x defines a first-class [`links`](https://spec.openapis.org/oas/v3.1.0#link-object) object precisely for this case — "given this response, here are operationIds you can follow next, and how to fill their parameters from the current response." We deliberately did not use it when the generic connector API shipped because our two consumers generate typed clients from the contract:

- **CLI** uses [Kiota](https://learn.microsoft.com/openapi/kiota/) (currently `microsoft.openapi.kiota` 1.30.0, `Microsoft.Kiota.Bundle` 1.21.2).
- **Web dashboard** uses [`openapi-typescript`](https://openapi-ts.dev/) (currently `^7.6.0`).

Both generators had incomplete support for `links` at the time the generic connector API landed: `links` entries generally flowed through to the emitted contract but did **not** produce typed, navigable follower helpers on the client. A consumer would still resolve the pointer by hand, so the typed client was no better off than with plain strings.

## Decision

**Stay with the plain URL-string fields for now.** No change to `ConnectorTypeResponse`, `UnitConnectorPointerResponse`, or the endpoints that populate them. This record is the canonical reference for *why* we are not using `links`, so future contributors don't re-open the question.

## Consequences

The current approach:

- Works today, typed by the same OpenAPI contract everyone else consumes.
- Is documented in `ConnectorModels.cs` via XML doc comments on each property.
- Trades standards-alignment for codegen ergonomics — we carry three lines of "here's the URL, go follow it yourself" logic on the client side.

What we give up until this is revisited:

- **Typed link-following helpers** (the client could expose something like `response.followLink("config", { unitId })` instead of string manipulation).
- **Operation-level guarantees** (the contract would express "this URL targets the `getUnitConnectorConfig` operation" — today that relationship lives only in prose).
- **One standard place for the relationship between operations**, which makes the contract more self-describing for third-party tooling (API explorers, doc generators).

The loss is small because (a) only two consumers exist and (b) both are internal, regenerated in CI from the same `openapi.json` checked into the repo.

## Revisit criteria

Reopen this decision when **both** of the following are true:

1. **Kiota emits typed link-follower helpers.** Concretely, a `links` entry in an OpenAPI response must produce a navigable method on the generated request builder (analogous to how path segments today produce builder chains), not just carry the metadata through to the lock file. Track [microsoft/kiota#2236](https://github.com/microsoft/kiota/issues/2236) and the Kiota changelog for "OpenAPI links" support. Re-check whenever we bump the `microsoft.openapi.kiota` tool version in `.config/dotnet-tools.json`.
2. **`openapi-typescript` (or the companion `openapi-fetch`) emits typed link helpers.** Today it emits `links?:` on response objects as an opaque map. Re-check whenever we bump `openapi-typescript` in `src/Cvoya.Spring.Web/package.json`.

Operationally: the next contributor who bumps either generator **should** re-read this record, confirm the state of the two items above, and — if both are satisfied — file a follow-up to migrate the three pointer fields to `links`. If only one is satisfied, keep the plain-string approach and note the state on the bump PR.

### Migration sketch (for when both conditions are met)

- Replace the three string properties on `ConnectorTypeResponse` / `UnitConnectorPointerResponse` with `links` entries on the relevant OpenAPI response objects, each referencing an `operationId` defined in the same contract. Parameters that today appear as path templates (`{unitId}`) become link `parameters` expressions (`$request.path.id` / `$response.body#/unitId` as appropriate).
- Regenerate the Kiota CLI client and the TypeScript client; delete the string-field fallbacks.
- Keep `configSchemaUrl` as-is unless the schema is also exposed as an addressable OpenAPI operation — it's a pointer to a static JSON document, not to an API operation, so `links` doesn't naturally fit.
- Bump the OpenAPI contract's minor version (the response shape changes) and note the break in `CHANGELOG.md`.

## Priority

Low. This is a quality-of-life improvement for contract consumers, not a functional gap. Scheduling follows the bump cycle of the two codegen tools rather than any product deadline.
