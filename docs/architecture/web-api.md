# Public Web API

The public Web API is the **tenant-facing contract surface** of Spring Voyage. The CLI builds on it, the portal builds on it, and per [ADR 0029](../decisions/0029-tenant-execution-boundary.md) tenant-scoped agent containers also call back into it (the A2A send is the single tenant→platform interface). Anything that doesn't go through this API is either operator-only (CLI mutations the portal exposes read-only views of) or platform-internal.

This document is the architecture-layer reference for **how the API is produced, where it's consumed, and how to evolve it**. Specific endpoint shapes live in the OpenAPI spec; the per-endpoint consumer reference (auth quick-start, common patterns, error envelope, and the rendered HTML view of the spec) lives at [`docs/api/`](../api/README.md). Deprecation policy, contract tests, and the operator/tenant authz boundary are tracked under sub-issues of [Area C / #1216](https://github.com/cvoya-com/spring-voyage/issues/1216).

## Source of truth

The OpenAPI spec is **code-first** with a committed snapshot.

```text
   .NET endpoints                   build-time emission                committed spec
┌─────────────────────────┐      ┌───────────────────────┐      ┌─────────────────────────────────────────────┐
│ src/Cvoya.Spring.Host.  │ ───► │ Microsoft.AspNetCore. │ ───► │ src/Cvoya.Spring.Host.Api/openapi.json      │
│   Api/Endpoints/*.cs    │      │ OpenApi  +  M.E.Api-  │      │  (regenerated on every build, committed     │
│ + Program.cs            │      │ DescriptionServer     │      │   to source so consumers can pin)           │
└─────────────────────────┘      └───────────────────────┘      └─────────────────────────────────────────────┘
                                                                                │
                                                                                ▼
                                                                       ┌────────────────┐
                                                                       │ openapi-drift  │   CI fails the PR if the
                                                                       │   CI job       │   working tree's spec
                                                                       │                │   diverges from code.
                                                                       └────────────────┘
                                                                                │
                                          ┌─────────────────────────────────────┴─────────────────────────────────────┐
                                          ▼                                                                           ▼
                                   ┌──────────────────────────────────┐                            ┌──────────────────────────────────┐
                                   │ Kiota client (CLI)               │                            │ openapi-typescript types (Portal)│
                                   │  src/Cvoya.Spring.Cli/Generated/ │                            │  src/Cvoya.Spring.Web/src/lib/   │
                                   │  (gitignored, regenerated each   │                            │    api/schema.d.ts (gitignored,  │
                                   │   build via GenerateKiotaClient) │                            │    regenerated on dev/build/test)│
                                   └──────────────────────────────────┘                            └──────────────────────────────────┘
```

**Key consequences:**

- **The spec follows the code.** A new endpoint exists in the spec because the build re-emitted it, not because someone hand-edited JSON. The committed `openapi.json` is a *snapshot* taken at the last commit on this branch.
- **CI guards drift.** The `openapi-drift` job in `.github/workflows/ci.yml` runs `dotnet build` and verifies that no working-tree changes resulted. If your endpoint code changed and you didn't commit the regenerated spec, CI fails the PR.
- **No hand-written spec.** Don't edit `openapi.json` directly — modify the C# endpoint, build, and commit the regenerated file.
- **Bindings regenerate on every build.** The CLI's Kiota client and the portal's TypeScript types are regenerated from the committed spec by build/test/dev hooks. Consumers pin to the committed spec, not to a moving spec server.

## Resource surface

The API namespace is single-versioned at `/api/v1/...`, scope-split into `/api/v1/platform/...`, `/api/v1/tenant/...`, and `/api/v1/webhooks/...`. There is no minor versioning today (no `v1.1`), no version negotiation header, and no deprecated-but-kept endpoints. The v0.1 freeze of this surface is recorded under [v1 freeze for v0.1](#v1-freeze-for-v01); `openapi.json` at the merge of [#1250](https://github.com/cvoya-com/spring-voyage/issues/1250) is the freeze artifact.

The resource groups (counts taken from the committed `openapi.json` at the v0.1 freeze; check the spec for live numbers as additive changes ship):

| Group | Tag | Endpoints | Verbs | What it covers |
| --- | --- | ---: | --- | --- |
| Units | `Units` | 23 | GET / POST / PATCH / DELETE | Tenant execution containers (orchestration roots), members, lifecycle, memberships, connector pointer, human permissions |
| Connectors (tenant) | `Connectors`, `Connectors.GitHub`, `Connectors.GitHub.OAuth`, `Connectors.Arxiv`, `Connectors.WebSearch` | 22 | GET / POST / PUT / PATCH / DELETE | External integrations (GitHub, ArXiv, web-search), per-tenant install / bind, per-unit config, credential validation |
| Agents | `Agents` | 13 | GET / POST / PATCH / PUT / DELETE | Agent definitions, lifecycle (deploy / scale / undeploy), execution, logs, skills, memberships, memories |
| Agent Runtimes (install) | `AgentRuntimes` | 9 | GET / POST / PATCH / DELETE | Per-tenant install rows for LLM-provider runtimes (Claude, OpenAI, Google, Ollama), config, credential health, model catalog |
| Secrets | `Secrets` | 14 | GET / POST / PUT / DELETE | Unit / tenant / platform-scoped secret CRUD + versioning + prune (uniform shape across all three scopes) |
| Initiative | `Initiative` | 5 | GET / PUT | Per-agent and per-unit initiative policy + agent's effective level |
| Threads | `Threads` | 4 | GET / POST | Tenant timeline of threads (participant-set per [ADR-0030](../decisions/0030-thread-model.md)), single-thread read, post-message, close |
| Cost & Budget | `Costs`, `Tenant`, `Budgets` | 8 | GET / PUT | Per-agent, per-unit, per-tenant cost; cost time series; per-agent / per-unit / per-tenant budgets |
| Cloning | `Clones`, `CloningPolicy` | 9 | GET / POST / PUT / DELETE | Per-agent clones; per-agent + tenant-wide cloning policy |
| Expertise | `Expertise` | 5 | GET / PUT | Per-agent expertise, per-unit own + aggregated expertise |
| Unit governance | `UnitPolicy`, `UnitBoundary`, `UnitOrchestration`, `UnitExecution` | 11 | GET / PUT / DELETE | Unit policy, boundary projection rules, orchestration strategy, execution defaults |
| Platform tenants | `PlatformTenants` | 4 | GET / POST / PATCH / DELETE | Platform-level tenant CRUD (PlatformOperator only) |
| Dashboard | `Dashboard` | 4 | GET | Summary, unit KPIs, agent metrics, cost rollup |
| Packages | `Packages` | 4 | GET | Installed-package + unit-template discovery |
| Activity | `Activity` | 2 | GET | Activity log query + SSE stream of tenant activity |
| Auth | `Auth` | 3 | GET / POST / DELETE | API token CRUD, current-user identity |
| System (platform) | `System` | 2 | GET | Provider credential status, startup configuration report |
| Directory | `Directory` | 3 | GET / POST | Tenant entry list, role lookup, expertise search |
| Analytics | `Analytics` | 2 | GET | Throughput counters, wait-time rollup |
| Messages | `Messages` | 2 | GET / POST | Routed message send, single-message lookup |
| Memories | `Agents`, `Units` | 2 | GET | Per-agent and per-unit memory peek (stub-empty until [ADR-0029](../decisions/0029-tenant-execution-boundary.md) Stage 4) |
| Skills | `Skills` | 1 | GET | Skill catalog (registered MCP-tool surface) |
| Inbox | `Inbox` | 1 | GET | Per-human inbox over the activity stream |
| Webhooks | `Webhooks` | 1 | POST | GitHub webhook ingest (HMAC-auth, not bearer) |
| Platform info | `Platform` | 1 | GET | Anonymous version / build-hash / license metadata |

Verbs are dominated by GET (read-dominant surface). The above table is grouped by OpenAPI tag; some tags fan out across multiple resource paths (e.g., `Units` covers `/api/v1/tenant/units`, `/api/v1/tenant/units/{id}/members`, `/api/v1/tenant/units/{id}/memberships`, etc.). The committed `openapi.json` is the canonical enumeration.

## Roles and URL scope

The public API is gated by **three authz roles**, applied per endpoint:

| Role | Scope | Examples |
| --- | --- | --- |
| `PlatformOperator` | The Spring Voyage platform itself | tenant CRUD, system credentials, platform secrets, runtime registration |
| `TenantOperator` | A tenant's configuration | runtimes / connectors install, secrets, GitHub App, BYOI, cloning policy, budget |
| `TenantUser` | Using SV in a tenant | messaging, observing, units / agents, dashboard, threads |

OSS overlay: every authenticated caller is granted all three claims (single-user OSS deployments). Cloud overlay: per-identity scoping.

The URL space is grouped by **scope**, with the major version *outside* the scope group so the contract version is a single number across the whole API:

```text
/api/v1/platform/...    →  PlatformOperator only
/api/v1/tenant/...      →  TenantOperator or TenantUser, per endpoint
```

`v1` covers both scope groups together — there is no independent `platform/v1` / `tenant/v1` evolution. A breaking change to either group bumps the whole API to `/api/v2/...`. See [Versioning and deprecation](#versioning-and-deprecation).

**Principle: if the CLI consumes an endpoint, it lives on the public API.** The CLI is the canonical mutation surface for operator workflows (per [`CONVENTIONS.md` § "UI / CLI Feature Parity"](../../CONVENTIONS.md)) and builds on the public Web API — there is no CLI-private API. An operator endpoint with no portal exposure still belongs in the public spec, gated to `PlatformOperator` or `TenantOperator`.

The boundary work — role definitions, URL restructure into `{platform,tenant}` scope groups, connector split, platform-tenant CRUD — landed under [#1247](https://github.com/cvoya-com/spring-voyage/issues/1247) and its sub-issues [#1257](https://github.com/cvoya-com/spring-voyage/issues/1257) – [#1260](https://github.com/cvoya-com/spring-voyage/issues/1260). The committed `openapi.json` reflects the scope-split layout the table above describes.

## Consumers

Three consumer classes:

### CLI

- **Generation:** Kiota generates a typed C# client from the committed spec into `src/Cvoya.Spring.Cli/Generated/` (gitignored). The build target is `GenerateKiotaClient`.
- **Hand-written wrapper:** `src/Cvoya.Spring.Cli/ApiClient.cs` exposes domain-specific methods (`ListAgentsAsync`, `DeployAgentAsync`, …) over the Kiota-generated client.
- **Auth:** `HttpClientRequestAdapter` injects the bearer token via `ClientFactory`.

### Portal (Next.js)

- **Generation:** `openapi-typescript` generates `src/Cvoya.Spring.Web/src/lib/api/schema.d.ts` (gitignored) from the committed spec via `npm run generate-api`.
- **Runtime client:** `openapi-fetch` — lightweight, type-checks against the generated schema.
- **Build hooks:** `pretypecheck`, `prebuild`, `pretest` regenerate types so the portal can never drift away from the committed spec.

### Tenant agent containers (per ADR 0029)

Agent containers running on the per-tenant network call into the platform via **A2A 0.3.x send** — a single endpoint that routes the message to the target peer (in-network where reachable, via the dispatcher proxy where it isn't). The wire protocol is HTTP/gRPC; per-language SDKs (Python, C#) wrap it for ergonomics. Tenant containers reach the API surface through the same authenticated public-API path as any other tenant→platform call (Caddy ingress in OSS, K8s ingress in cloud).

## Adding a new endpoint

1. Add the route in the relevant `src/Cvoya.Spring.Host.Api/Endpoints/<Resource>Endpoints.cs`. Follow the existing pattern (route group, authz attribute, request/response model, `Produces<T>`).
2. Build: `dotnet build SpringVoyage.slnx`. The post-build step regenerates `src/Cvoya.Spring.Host.Api/openapi.json`.
3. Commit the regenerated spec. CI's `openapi-drift` job will reject the PR otherwise.
4. The CLI's Kiota client and the portal's TypeScript types regenerate on the next build / dev / test invocation — no manual step.
5. Pick the right scope group (`/api/v1/platform/...` for `PlatformOperator`-gated, `/api/v1/tenant/...` for tenant-scoped) and apply the role gate explicitly. See [Roles and URL scope](#roles-and-url-scope).
6. If the endpoint introduces a breaking change to an existing one, see [Versioning and deprecation](#versioning-and-deprecation) below — breaking changes don't ship inside `v1`.

## Versioning and deprecation

The contract for how `v1` evolves and what triggers `v2`. This applies to every endpoint under `/api/v1/...` once it has shipped at least once on `main`.

### Stability levels

There are exactly two:

- **Stable** — every endpoint under `/api/v1/`. Conforming consumers can rely on its shape, status codes, and behaviour for the lifetime of `v1`.
- **Removed** — gone from `v1`; lives only in the next major (`v2`) if at all.

There is no `experimental`, no `beta`, no `preview` tier. Ship endpoints when they're stable. If a feature genuinely needs to be tested in production before commitment, file an ADR proposing how to do that without polluting `/api/v1/`.

### What counts as a breaking change

Any change that can break a conforming consumer, including:

- Removing an endpoint, route, or query parameter.
- Removing a property from a response schema.
- Adding a *required* property to a request schema.
- Changing the type of a request or response property (including narrowing — e.g. `string | null` → `string`).
- Changing the meaning of an existing field, status code, or error envelope.
- Tightening validation on an existing request (rejecting input that was previously accepted).

Anything that breaks a consumer that was correct against the previous spec is breaking. When in doubt, treat it as breaking.

### What is *not* a breaking change

- Adding a new endpoint.
- Adding an *optional* property to a request schema.
- Adding a property to a response schema (consumers must ignore unknown properties).
- Adding a new status code that maps to a new condition the consumer wouldn't have hit before.
- Adding a new enum value (consumers must ignore unknown values; the spec encodes this by listing only known values without forbidding others).

These are additive and ship inside `v1` without a version bump.

### v1 is strictly additive

`v1` does **not** evolve through minor versions in the URL space. There is no `/api/v1.1/...`. Every additive change ships transparently to consumers; every breaking change waits for `v2`.

If `v2` happens, it lives at `/api/v2/...` parallel to `v1` for the duration of the `v1` deprecation window. Consumers migrate at their own pace; the platform doesn't switch them. The shape of `v2` is out of scope for this document — file an ADR when proposing it.

### Deprecation signals

When an endpoint is scheduled for removal in the next major:

1. **OpenAPI flag.** Mark the operation with `deprecated: true` in the spec. The build emits this from C# attributes — annotate the endpoint with `[Obsolete("Removed in v2; use /api/v2/...")]` (or the equivalent route metadata).
2. **HTTP response header.** Every response from a deprecated endpoint includes `Deprecation: true` and `Sunset: <RFC 8594 date>` pointing at the planned removal date.
3. **CLI / portal surface.** The Kiota client and `openapi-typescript` types pick the `deprecated` flag up automatically on the next build. CLI command help should warn when invoking a deprecated path; portal data hooks should log a deprecation notice in non-production builds.

### Sunset window

A deprecated endpoint stays available for **at least 90 days** after the deprecation lands on `main`. Removal happens in `v2`, never inside `v1`. Consumers see deprecation flags and `Sunset` headers throughout the window so they can plan migration.

If an endpoint must be removed before the 90-day window is up (security incident, severe data exposure), the removal is treated as a breaking-change carve-out and gets a security advisory rather than a normal release note. This should be very rare.

### Practical consequences

- A PR that touches a public endpoint shape **and** doesn't deprecate the previous shape is a breaking-change PR; reviewers should reject it for `v1`.
- The `openapi-drift` CI job catches accidental shape changes; reviewers catch intentional ones.
- The freeze of `v1` for v0.1 ([#1250](https://github.com/cvoya-com/spring-voyage/issues/1250)) commits the platform to this policy; once v0.1 ships, breaking the policy means breaking real consumers.

## v1 freeze for v0.1

The merged commit of [#1250](https://github.com/cvoya-com/spring-voyage/issues/1250) is the v1 contract freeze for v0.1. The `openapi.json` artifact at that commit is the authoritative enumeration of every public endpoint shipping in v1; the policy in [Versioning and deprecation](#versioning-and-deprecation) governs every change after the freeze lands.

**What's in v1 (high-level inventory).** The [Resource surface](#resource-surface) table enumerates the v1 surface by tag. Every endpoint in the committed `openapi.json` is *in* v1; nothing is held back as `experimental` / `beta` / `preview` (those tiers do not exist — see [Stability levels](#stability-levels)). Anything that needed to ship after the freeze must follow the [What is *not* a breaking change](#what-is-not-a-breaking-change) rules to land additively inside `v1`, or wait for `v2`.

**What's *not* in v1.** Two adjacent surfaces are deliberately excluded:

- **Bucket-1 SDK contract** (`initialize` / `on_message` / `on_shutdown` lifecycle hooks) per [ADR-0029](../decisions/0029-tenant-execution-boundary.md) is an embeddable agent-side contract, *not* a public Web API surface. The [agent-runtime boundary spec](../specs/agent-runtime-boundary.md) carries its own version posture (currently `v0.1 (initial)` per its § 7 changelog) and does not ride the `/api/v1/...` URL space.
- **Bucket-2 send endpoint** at `POST /api/v1/threads/{thread_id}/messages` is in v1 today — see the `Threads` row of the [Resource surface](#resource-surface) table — and any future MCP tool surfaces (memory `store` / `recall`, `peek_pending`, `message.retract`, future timers / pub-sub / cloning per ADR-0029 § "Capabilities reached through MCP") will be added additively inside `v1` as they land per the [agent-runtime boundary spec § 6](../specs/agent-runtime-boundary.md#6-out-of-scope).

**Deprecation status at the freeze.** No endpoint in the v1 freeze carries `deprecated: true` or a `Sunset` header. This is deliberate: per [ADR-0030](../decisions/0030-thread-model.md) Q10 ("no migration to v0.1") the schema and URL surface took full freedom inside v0.1, so the freeze ships clean. The legacy `/conversations` URL space was never aliased on `v1` — `/api/v1/tenant/threads/...` is the canonical (and only) URL surface for the thread model.

**What this commits the platform to.** Once the freeze lands:

- Removing any endpoint, removing a property from any response schema, or making any other change in the [What counts as a breaking change](#what-counts-as-a-breaking-change) list waits for `v2`.
- Adding new endpoints, adding optional request properties, adding response properties, and adding new enum values (per [What is *not* a breaking change](#what-is-not-a-breaking-change)) ship transparently inside `v1`.
- The consumer-facing publication of the frozen spec lives at [`docs/api/`](../api/README.md): a Markdown index plus a Redoc-rendered HTML reference (`docs/api/v1.html`), regenerated from `openapi.json` on demand locally and produced as a CI workflow artifact on every change.

## Cross-references

- Endpoint surface is enumerated in [`src/Cvoya.Spring.Host.Api/openapi.json`](../../src/Cvoya.Spring.Host.Api/openapi.json) (the v1 freeze artifact at the merge of [#1250](https://github.com/cvoya-com/spring-voyage/issues/1250)).
- Consumer-facing per-endpoint reference: [`docs/api/`](../api/README.md) — Markdown index + Redoc-rendered HTML view of `openapi.json`. The HTML is regenerated from the spec on demand (`npm --workspace=spring-voyage-dashboard run generate-api-docs`) and uploaded as a CI workflow artifact on every change.
- Tenant→platform interface contract: [ADR 0029](../decisions/0029-tenant-execution-boundary.md), [agent-runtime boundary spec](../specs/agent-runtime-boundary.md).
- Thread / Engagement / Collaboration framing the URL surface anchors on: [ADR 0030](../decisions/0030-thread-model.md), [thread-model design](thread-model.md).
- LLM access via the API (platform-level, not per-tenant): [ADR 0028 amendment](../decisions/0028-tenant-scoped-runtime-topology.md).
- Operator carve-out: [`CONVENTIONS.md` § "UI / CLI Feature Parity"](../../CONVENTIONS.md), [`AGENTS.md` § "Operator surfaces"](../../AGENTS.md).
- Open Area C work: [#1216](https://github.com/cvoya-com/spring-voyage/issues/1216) (umbrella) and its sub-issues; consumer-facing publication of the frozen spec landed under [#1251](https://github.com/cvoya-com/spring-voyage/issues/1251) (C2.2) — see [`docs/api/`](../api/README.md).
