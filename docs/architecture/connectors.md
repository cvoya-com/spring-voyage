# Connectors

> **[Architecture index](README.md)** · Related: [Runtime flows](runtime-flows.md), [Units & agents](units-and-agents.md), [Packages](packages.md), [Security](security.md)

A connector bridges an external system (GitHub, Slack, arXiv, …) to a unit. It
is **domain-agnostic infrastructure**: connectors facilitate the flow of events
and actions, but the platform does not replicate the upstream system's own
configuration model ([ADR-0045](../decisions/0045-connector-domain-agnostic-platform.md)).

A connector is a **non-routable bridge** — it is not an actor, and nothing
routes a domain message to it ([ADR-0053](../decisions/0053-units-are-agents-and-one-way-delivery.md)).

---

## Two surfaces, and only two

| Surface | What it does |
|---------|--------------|
| **Inbound** | An event translator. It receives external events (webhooks, polled feeds) and translates each into a one-way domain message addressed at the bound unit. |
| **Outbound** | An agent-invoked skill. A connector may register an `ISkillRegistry` whose tools (named `<connector-slug>.*`) appear in an agent's MCP tool surface alongside the platform `sv.*` tools. |

A connector that registers no skill registry still functions — inbound events
flow, lifecycle hooks fire. A connector never exposes a message-receiving
interface.

## The `IConnectorType` plugin contract

A connector ships as its own project (`Cvoya.Spring.Connector.<Name>`),
implements `IConnectorType`, and registers through one `AddCvoyaSpringConnector<Name>()`
DI extension. Host code references only the abstraction; the catalogue and
binding surfaces pick up a new connector automatically. The contract carries:

- a stable `TypeId`, a URL-safe `Slug`, and a `ToolNamespace`;
- a unit-binding config schema (`ConfigType`) and an optional display-identity
  user-config schema (`UserConfigType`);
- relative HTTP routes scoped under `/api/v1/connectors/{slug}`;
- lifecycle hooks — `OnUnitStartingAsync` (register external resources, e.g.
  webhooks), `OnUnitStoppingAsync` (tear them down);
- optional health hooks — `ValidateCredentialAsync`, `VerifyContainerBaselineAsync`
  (both no-op by default).

### Built-in connectors

| Slug | Scope |
|------|-------|
| `github` | App / PAT auth, webhook ingest + filtering, binding lifecycle, label-roundtrip, per-launch runtime-context contribution. **Event-only at the platform-MCP boundary** — no `github.*` MCP tools; agents run `gh` / `git` directly in-container. |
| `arxiv` | Read-only literature search; no auth, no webhooks |
| `web-search` | A façade over a pluggable `IWebSearchProvider` (Brave by default); API keys resolved by secret name at invoke time |

## Connector bindings

A unit is bound to a connector type by a binding row in `unit_connector_bindings`
— one binding per connector type per unit, carrying the typed config. Bindings
**inherit** down the unit hierarchy: the binding-resolution walk climbs from a
unit toward the tenant root and the closest binding wins. Binding a connector
**auto-grants** its tools — one `<ToolNamespace>.*` row per bind into the unit's
tool grants — and revokes them on unbind, so member agents inherit connector
tools through their unit memberships with no per-agent wiring.

A connector binding declares only what the unit needs to *participate*. It does
**not** replicate the upstream system's subscription model (App installations,
channel invites). The GitHub webhook handler keys inbound events on
`(owner, repo)` within the receiving tenant and fans out to every matching
binding.

## Runtime-context contribution

A bound connector can deliver identity and a short-lived **outbound bearer
token** into the runtime container by implementing
`IConnectorRuntimeContextContributor`. The dispatcher resolves every applicable
binding (direct or inherited), invokes each contributor at launch, and merges
the result into the launch spec — env vars under `SPRING_CONNECTOR_<SLUG>_*`,
context files under `.spring/connectors/<slug>/` (workspace-relative; the
`.spring/` namespace ADR-0058 reserves for platform-controlled files).
Credentials are resolved per launch
and never cached across launches; rotation is handled by relaunching. A sibling
prompt-context contributor renders a markdown fragment into the agent's prompt
naming the bound resource and the env vars its container carries.

For GitHub, the token dispatches on the binding's pinned auth choice
([ADR-0047](../decisions/0047-platform-user-human-split.md) §6): a freshly-minted
installation access token on the App branch, or the binding's PAT secret on the
PAT branch.

## Connector identity vs. credentials

Two distinct things, kept apart:

- **Outbound credentials** live on the **unit binding** — the binding's pinned
  credential is what every outbound call uses, regardless of caller.
- **Display identity** (a GitHub login, a Slack handle the agent renders in
  `@`-mentions) lives on the **`TenantUser`**, in `TenantUserConnectorIdentity`
  — a strictly display-only row, no auth fields. See
  [Data & identity](data-and-identity.md).

## Disabled-with-reason

A connector classifies its environment configuration at startup into **valid**,
**missing**, or **malformed**. Missing config disables the connector with a
human-readable reason — connector-scoped endpoints short-circuit to a structured
`404 { "disabled": true, "reason": "…" }` and the platform still boots.
Malformed config is a fatal startup error (fail-fast). This composes with the
startup configuration validator described in [Deployment](deployment.md).
