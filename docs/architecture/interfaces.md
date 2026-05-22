# Interfaces

> **[Architecture index](README.md)** · Related: [Components](components.md), [Security](security.md), [Observability](observability.md)

Spring Voyage exposes one API and two clients on top of it: the `spring` CLI and
the web portal. Everything a user can do goes through the public Web API — there
is no client-private data path.

---

## The Web API

`spring-api` (the HTTP front door — see [Components](components.md)) serves a
versioned REST API under `/api/v1/`. It is the single source of truth for every
client. Endpoints are grouped by concern:

| Group | Covers |
|-------|--------|
| Agents, units, memberships | Definitions, lifecycle, members, boundary, policy |
| Threads, messages, inbox | The participant-set thread surface and message dispatch |
| Packages, installs | Catalogue, install (two-phase), export |
| Connectors, secrets | Connector catalogue and bindings, the secrets registry |
| Activity, analytics, dashboard, costs, budgets, issues | The observability and cost surfaces |
| Model providers, system, platform tenants | Operator configuration |
| Auth, tenant users, identities | Token management, connector display identity |
| Webhooks, OTLP ingest | External ingress — HMAC / per-invocation JWT auth |

Authentication and the platform-role gates (`PlatformOperator` / `TenantOperator`
/ `TenantUser`) are described in [Security](security.md). Webhook and OTLP-ingest
routes sit outside the role-gated groups — they are external ingress with their
own auth.

### The OpenAPI contract

The API emits an OpenAPI document (.NET 10 native OpenAPI; the document is a
build artefact). Both clients are generated from it: the CLI from a **Kiota**
C# client, the portal from a TypeScript client. The committed `openapi.json` is
the contract; a CI drift check fails a PR whose code and spec disagree. Every
public `Guid` rides the wire in the canonical form described in
[Data & identity](data-and-identity.md).

## The `spring` CLI

The `spring` CLI (`Cvoya.Spring.Cli`) is the primary operator and power-user
surface. It is built **entirely on the generated Kiota client** — every
mutation goes through the typed `SpringApiClient`, never a raw `HttpClient`, so
the CLI cannot silently drift from the OpenAPI contract. Command groups mirror
the API: `spring agent`, `spring unit`, `spring package`, `spring connector`,
`spring thread`, `spring message`, `spring activity`, `spring secret`,
`spring cost`, `spring user`, `spring auth`, and more, each with verbs
(`create`, `list`, `show`, …).

A resolver accepts either a `Guid` (direct lookup) or a display name (search
with optional `--unit` scoping) on every `show` verb — a Guid-shaped token is
always treated as identity.

## The web portal

The portal (`Cvoya.Spring.Web`, Next.js in `standalone` mode) is a pure client
of the Web API. It is a **two-portal architecture**
([ADR-0033](../decisions/0033-two-portal-architecture.md)):

- **Management portal** — agents, units, connectors, packages, installs, skills,
  analytics, budgets, policies, humans, activity, discovery, settings.
- **Engagement portal** (`/engagement/**`) — the participant-facing surface for
  inbox, activity, and thread/collaboration views.

Both portals share one Next.js application, one session, one API client, and
one design-token set. Neither has a portal-private API — they consume the same
endpoints the CLI consumes.

## UI / CLI parity

Every **user-facing** feature is reachable identically from the portal and the
CLI; the two are kept in lock-step and the parity is a hard rule in
`CONVENTIONS.md`. The contract is the shared API: a feature is a set of
endpoints, and both clients consume them.

**Operator surfaces are CLI-only by design.** Operational configuration —
agent-runtime config, connector config, credential health, tenant seeds,
skill-bundle bindings — is mutated only through the `spring` CLI. The portal MAY
render read-only views of operator state for visibility, but every mutation goes
through the CLI. User-facing features remain strictly parity-bound.
