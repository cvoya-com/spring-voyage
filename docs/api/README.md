# Spring Voyage Public Web API — v1 Reference

This directory is the **consumer-facing reference** for the Spring Voyage public Web API. It is the published view of the v1 contract that ships with v0.1.

If you're an external developer integrating with Spring Voyage — building a CLI, a portal, an automation, or a tenant agent that calls back into the platform — start here.

If you're a contributor changing the API, start at [`docs/architecture/web-api.md`](../architecture/web-api.md) instead. That document is the architecture-layer reference (how the spec is produced, how it evolves, what the freeze commits the platform to). The reference here is generated from the same source of truth.

## Contents

- **`v1.html`** — full per-endpoint reference rendered from the frozen `openapi.json`. Self-contained HTML: no network calls, no external assets.
  - **Locally:** `npm --workspace=spring-voyage-dashboard run generate-api-docs` writes `docs/api/v1.html`. Open in a browser.
  - **From CI:** the `web-api-docs` job (`.github/workflows/ci.yml`) re-runs the build on every change to `openapi.json` and uploads the HTML as a workflow artifact named `web-api-docs`. Download from the run page.
  - The file is **not committed** — it's a generated view of `openapi.json` and tracked the same way as the CLI Kiota client and the portal TypeScript schema (gitignored, regenerated on demand).
- [`../../src/Cvoya.Spring.Host.Api/openapi.json`](../../src/Cvoya.Spring.Host.Api/openapi.json) — the OpenAPI 3.1.1 spec itself. Pin against this file if you generate your own client.

## Source of truth

The v1 spec is **code-first**. The .NET endpoints under `src/Cvoya.Spring.Host.Api/Endpoints/*.cs` are the authority; the build emits `openapi.json` after every `dotnet build`; CI rejects PRs whose working tree drifts. The generated HTML reference here is rebuilt from `openapi.json`, so the per-endpoint detail you read in `v1.html` is whatever shipped on `main`.

See [`docs/architecture/web-api.md` § Source of truth](../architecture/web-api.md#source-of-truth) for the full pipeline diagram.

## v1 freeze

The v1 contract was frozen for the v0.1 release. The committed `openapi.json` at the freeze commit is the authoritative enumeration of every public endpoint in v1; the [Versioning and deprecation](../architecture/web-api.md#versioning-and-deprecation) policy governs every change after.

In short: **v1 is strictly additive.** Breaking changes wait for v2; new endpoints, new optional request properties, new response properties, and new enum values ship transparently inside v1. Conforming consumers can rely on every documented shape, status code, and behaviour for the lifetime of v1.

## Resource groups

The full per-endpoint reference is in `v1.html` (see [Contents](#contents) above for how to obtain it). The high-level inventory below mirrors the table in [`docs/architecture/web-api.md` § Resource surface](../architecture/web-api.md#resource-surface) — refer back to that document for live counts as additive changes ship.

| Group | Tag | What it covers |
| --- | --- | --- |
| Units | `Units` | Tenant execution containers (orchestration roots), members, lifecycle, memberships, connector pointer, human permissions |
| Connectors (tenant) | `Connectors`, `Connectors.GitHub`, `Connectors.GitHub.OAuth`, `Connectors.Arxiv`, `Connectors.WebSearch` | External integrations (GitHub, ArXiv, web-search), per-tenant install / bind, per-unit config, credential validation |
| Agents | `Agents` | Agent definitions, lifecycle (deploy / scale / undeploy), execution, logs, skills, memberships, memories |
| Agent Runtimes | `AgentRuntimes` | Per-tenant install rows for LLM-provider runtimes (Claude, OpenAI, Google, Ollama), config, credential health, model catalog |
| Secrets | `Secrets` | Unit / tenant / platform-scoped secret CRUD + versioning + prune (uniform shape across all three scopes) |
| Initiative | `Initiative` | Per-agent and per-unit initiative policy + agent's effective level |
| Threads | `Threads` | Tenant timeline of threads (participant-set per [ADR-0030](../decisions/0030-thread-model.md)), single-thread read, post-message, close |
| Cost & Budget | `Costs`, `Tenant`, `Budgets` | Per-agent, per-unit, per-tenant cost; cost time series; per-agent / per-unit / per-tenant budgets |
| Cloning | `Clones`, `CloningPolicy` | Per-agent clones; per-agent + tenant-wide cloning policy |
| Expertise | `Expertise` | Per-agent expertise, per-unit own + aggregated expertise |
| Unit governance | `UnitPolicy`, `UnitBoundary`, `UnitOrchestration`, `UnitExecution` | Unit policy, boundary projection rules, orchestration strategy, execution defaults |
| Platform tenants | `PlatformTenants` | Platform-level tenant CRUD (PlatformOperator only) |
| Dashboard | `Dashboard` | Summary, unit KPIs, agent metrics, cost rollup |
| Packages | `Packages` | Installed-package + unit-template discovery |
| Activity | `Activity` | Activity log query + SSE stream of tenant activity |
| Auth | `Auth` | API token CRUD, current-user identity |
| System (platform) | `System` | Provider credential status, startup configuration report |
| Directory | `Directory` | Tenant entry list, role lookup, expertise search |
| Analytics | `Analytics` | Throughput counters, wait-time rollup |
| Messages | `Messages` | Routed message send, single-message lookup |
| Memories | `Agents`, `Units` | Per-agent and per-unit memory peek (stub-empty until [ADR-0029](../decisions/0029-tenant-execution-boundary.md) Stage 4) |
| Skills | `Skills` | Skill catalog (registered MCP-tool surface) |
| Inbox | `Inbox` | Per-human inbox over the activity stream |
| Webhooks | `Webhooks` | GitHub webhook ingest (HMAC-auth, not bearer) |
| Platform info | `Platform` | Anonymous version / build-hash / license metadata |

## URL scope and authorization

The API namespace is single-versioned at `/api/v1/...` and split into three scope groups:

| URL prefix | Required role | Examples |
| --- | --- | --- |
| `/api/v1/platform/...` | `PlatformOperator` | tenant CRUD, system credentials, platform secrets, runtime registration |
| `/api/v1/tenant/...` | `TenantOperator` or `TenantUser` (per endpoint) | runtimes / connectors install, secrets, units, agents, threads, dashboard |
| `/api/v1/webhooks/...` | HMAC-signed (not bearer) | external system event ingest (e.g., GitHub) |

In OSS deployments every authenticated caller is granted all three role claims. The hosted Spring Voyage service scopes per-identity. See [`docs/architecture/web-api.md` § Roles and URL scope](../architecture/web-api.md#roles-and-url-scope) for the full role taxonomy.

## Authentication quick-start

Most endpoints use **bearer-token authentication** with a Spring Voyage API Token (SVAT). Issue a token via the API:

```bash
# 1. Mint a token (the response includes a one-shot `value` field)
curl -X POST http://localhost:5000/api/v1/tenant/auth/tokens \
  -H "Authorization: Bearer <your-existing-token>" \
  -H "Content-Type: application/json" \
  -d '{"name": "my-cli-token"}'

# Response:
# {
#   "name": "my-cli-token",
#   "value": "svat_...",
#   "createdAt": "2026-04-28T..."
# }

# 2. Use the token in the Authorization header on subsequent requests
curl http://localhost:5000/api/v1/tenant/auth/me \
  -H "Authorization: Bearer svat_..."
```

The `value` field is returned **only** at creation time. Store it; it cannot be retrieved later. Revoke a token via `DELETE /api/v1/tenant/auth/tokens/{name}`.

The CLI (`spring`) and the portal handle token storage automatically. Hand-rolled clients should treat the token like any other secret.

Webhook endpoints (`/api/v1/webhooks/...`) authenticate via HMAC signatures from the source system, not bearer tokens. See the per-connector documentation under [`docs/architecture/connectors.md`](../architecture/connectors.md) for the per-source signing contract.

## Common patterns

### Deploy an agent

```bash
# Create the agent definition
curl -X POST http://localhost:5000/api/v1/tenant/agents \
  -H "Authorization: Bearer svat_..." \
  -H "Content-Type: application/json" \
  -d '{
    "name": "backend-engineer",
    "instructions": "...",
    "execution": {"tool": "claude-code"}
  }'

# Deploy it
curl -X POST http://localhost:5000/api/v1/tenant/agents/{id}/deploy \
  -H "Authorization: Bearer svat_..."
```

### Send a message on a thread

```bash
# Open a thread by posting to it (threads are participant-sets per ADR-0030)
curl -X POST http://localhost:5000/api/v1/tenant/threads/{thread_id}/messages \
  -H "Authorization: Bearer svat_..." \
  -H "Content-Type: application/json" \
  -d '{
    "from": "agent:human/me",
    "text": "Hello"
  }'

# Read the thread
curl http://localhost:5000/api/v1/tenant/threads/{thread_id} \
  -H "Authorization: Bearer svat_..."
```

The thread surface is the canonical way to drive agents from outside the platform. See [`docs/architecture/thread-model.md`](../architecture/thread-model.md) for the full participant-set semantics, and [ADR-0030](../decisions/0030-thread-model.md) for the rationale.

### Watch tenant activity

`/api/v1/tenant/activity/stream` is a Server-Sent Events endpoint over the tenant activity log. Subscribe with any SSE-aware client:

```bash
curl -N http://localhost:5000/api/v1/tenant/activity/stream \
  -H "Authorization: Bearer svat_..."
```

## Error envelope

Errors are returned as RFC 7807 `application/problem+json` documents with the standard fields (`type`, `title`, `status`, `detail`, `instance`). Schema-validation failures populate the `errors` field with per-field detail. Status codes follow REST conventions:

| Status | Meaning |
| --- | --- |
| `400` | Malformed request — bad JSON, missing required body field, invalid query parameter |
| `401` | Missing / invalid bearer token |
| `403` | Token is valid but lacks the required role for this endpoint (`TenantOperator` / `PlatformOperator`) |
| `404` | The addressed resource does not exist (or is not visible to the calling tenant) |
| `409` | Conflict — duplicate name, version mismatch, illegal state transition |
| `422` | Semantically invalid request — validation passed at the schema layer but the request is rejected by domain rules |
| `429` | Rate-limited (where applicable) |
| `5xx` | Platform-side failure |

The 404 / 403 distinction is deliberate: 404 hides existence (so a token in tenant A can't probe tenant B's resources), 403 says "you can see this exists but not act on it." Don't leak existence by inferring from a 403.

## Out of scope for this reference

- **Agent SDK contract** (Bucket 1 — the `initialize` / `on_message` / `on_shutdown` lifecycle hooks) is an embeddable agent-side surface, not a public Web API. See [`docs/specs/agent-runtime-boundary.md`](../specs/agent-runtime-boundary.md) for the full contract specification.
- **Agent runtime workflow protocol** (per-tool launch contract, container conformance) lives under [`docs/architecture/agent-runtime.md`](../architecture/agent-runtime.md).
- **CLI command reference.** The `spring` CLI builds on this API; its own help (`spring --help`, `spring <command> --help`) is the authoritative reference for the CLI surface.

## Generation pipeline

The HTML reference is regenerated from `openapi.json` by the `generate-api-docs` script in [`src/Cvoya.Spring.Web/package.json`](../../src/Cvoya.Spring.Web/package.json), which invokes [`@redocly/cli build-docs`](https://redocly.com/docs/cli/commands/build-docs):

```bash
npm --workspace=spring-voyage-dashboard run generate-api-docs
```

That writes `docs/api/v1.html` (gitignored — see [Contents](#contents)).

CI runs the same script in the `web-api-docs` job under `.github/workflows/ci.yml` whenever `openapi.json` changes (or this README, or the docs workflow itself). The job uploads the rendered HTML as a workflow artifact named `web-api-docs`, retained for 30 days, so a consumer who doesn't want to clone the repo can still fetch the rendered reference for any commit on `main`.

Because `v1.html` is generated from `openapi.json` on every consumer build, drift is structurally impossible: there is no committed HTML file that could fall behind the spec. The artefact is always whatever the script produces from the spec at HEAD.

## Cross-references

- [`docs/architecture/web-api.md`](../architecture/web-api.md) — architecture-layer reference: how the spec is produced, where it's consumed, how it evolves, the v1 freeze and the versioning policy that governs change.
- [`docs/specs/agent-runtime-boundary.md`](../specs/agent-runtime-boundary.md) — the agent SDK contract and the Bucket-2 send-endpoint contract from the agent-runtime side.
- [`docs/decisions/0029-tenant-execution-boundary.md`](../decisions/0029-tenant-execution-boundary.md), [`docs/decisions/0030-thread-model.md`](../decisions/0030-thread-model.md) — the ADRs behind the thread / participant-set surface and the tenant→platform boundary.
- [`src/Cvoya.Spring.Host.Api/openapi.json`](../../src/Cvoya.Spring.Host.Api/openapi.json) — the OpenAPI source of truth.
