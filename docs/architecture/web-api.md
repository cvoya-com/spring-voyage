# Public Web API

The public Web API is the **tenant-facing contract surface** of Spring Voyage. The CLI builds on it, the portal builds on it, and per [ADR 0029](../decisions/0029-tenant-execution-boundary.md) tenant-scoped agent containers also call back into it (the A2A send is the single tenant→platform interface). Anything that doesn't go through this API is either operator-only (CLI mutations the portal exposes read-only views of) or platform-internal.

This document is the architecture-layer reference for **how the API is produced, where it's consumed, and how to evolve it**. Specific endpoint shapes live in the OpenAPI spec; deprecation policy, contract tests, and the operator/tenant authz boundary are tracked under sub-issues of [Area C / #1216](https://github.com/cvoya-com/spring-voyage/issues/1216).

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

The API namespace is single-versioned at `/api/v1/...`. There is no minor versioning today (no `v1.1`), no version negotiation header, and no deprecated-but-kept endpoints.

The 20 resource groups (counts as of the v0.1 audit; check the spec for the live numbers):

| Group | Endpoints | Verbs | What it covers |
| --- | ---: | --- | --- |
| Units | 30 | GET / POST / PATCH / DELETE | Tenant execution containers (orchestration roots), configuration, lifecycle |
| Connectors | 22 | GET / POST / PATCH / DELETE | External integrations (GitHub, ArXiv, web-search), credential management, unit bindings |
| Agents | 18 | GET / POST / PUT / DELETE | Agent definitions, lifecycle (deploy / undeploy), execution, logs, skills, budget |
| Tenant | 8 | GET / POST / PUT | Tenant-scoped secrets, budget, cloning policy, hierarchy tree, cost time series |
| Agent Runtimes | 8 | GET / POST / DELETE | LLM provider registration (Claude, OpenAI, Google, Ollama), credential health, model list |
| Platform | 5 | GET / POST / DELETE | Anonymous metadata (info / version), operator secrets, secret versioning |
| Dashboard | 4 | GET | Summary, unit KPIs, agent metrics, cost rollup |
| Conversations | 4 | GET / POST | Conversation CRUD, message list, close |
| Directory | 3 | GET | Tenant user / role directory, search |
| Costs | 3 | GET | Per-agent, per-unit, per-tenant cost queries |
| Auth | 3 | GET / POST / DELETE | API token CRUD, identity verification |
| System | 2 | GET | Configuration report (startup probes), provider credential status |
| Analytics | 2 | GET | Throughput histogram, wait-time percentiles |
| Activity | 2 | GET | Audit log, SSE stream of tenant activity |
| Messages | 2 | GET | Conversation message query, routing history |
| Webhooks | 1 | POST | GitHub webhook ingest |
| Skills | 1 | GET | MCP endpoint URLs + schema |
| Inbox | 1 | GET | Tenant inbox (unread messages, action items) |
| Ollama | 1 | GET | Model availability — under review per [ADR 0028 amendment](../decisions/0028-tenant-scoped-runtime-topology.md), the LLM service is platform-level, not Ollama-specific |

Verb distribution: 92 GET, 32 POST, 22 PUT, 20 DELETE, 5 PATCH — read-dominant.

## Operator vs tenant boundary

Per [ADR 0029](../decisions/0029-tenant-execution-boundary.md), the public API is the **tenant→platform** interface. Operator-only surfaces (deployment, infrastructure provisioning, system configuration) are CLI-only by design — see the operator carve-out in [`CONVENTIONS.md` § "UI / CLI Feature Parity"](../../CONVENTIONS.md). Several endpoints currently sit ambiguously between the two; the audit and gating work is tracked in [#1247](https://github.com/cvoya-com/spring-voyage/issues/1247).

Until [#1247](https://github.com/cvoya-com/spring-voyage/issues/1247) resolves, treat the following as candidates for *operator-only role gating* (or removal from the public spec):

- `/api/v1/system/*` — startup probe report, provider credential status.
- `/api/v1/platform/secrets/*` — operator-level secrets (parallel to tenant secrets).
- `/api/v1/connectors/{slug}/install` — provisioning a connector type for the platform (vs binding a unit to a pre-installed connector).
- `/api/v1/dashboard/*` — scoping intent (tenant-self-view vs operator-cross-tenant) is unspecified.
- `/api/v1/activity/stream` (SSE) — same scoping question.
- `/api/v1/ollama/models` — superseded by the platform-level LLM invocation surface per the ADR-0028 amendment.

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
5. If the endpoint changes the *operator vs tenant* posture, mention it in the PR body so the [#1247](https://github.com/cvoya-com/spring-voyage/issues/1247) audit captures it.
6. If the endpoint introduces a breaking change to an existing one, see the deprecation policy ([#1249](https://github.com/cvoya-com/spring-voyage/issues/1249) — pending).

## Cross-references

- Endpoint surface is enumerated in [`src/Cvoya.Spring.Host.Api/openapi.json`](../../src/Cvoya.Spring.Host.Api/openapi.json).
- Tenant→platform interface contract: [ADR 0029](../decisions/0029-tenant-execution-boundary.md).
- LLM access via the API (platform-level, not per-tenant): [ADR 0028 amendment](../decisions/0028-tenant-scoped-runtime-topology.md).
- Operator carve-out: [`CONVENTIONS.md` § "UI / CLI Feature Parity"](../../CONVENTIONS.md), [`AGENTS.md` § "Operator surfaces"](../../AGENTS.md).
- Open Area C work: [#1216](https://github.com/cvoya-com/spring-voyage/issues/1216) (umbrella) and its sub-issues.
