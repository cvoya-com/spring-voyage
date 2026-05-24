# Authoring a Connector

This guide is for developers adding a new connector to Spring Voyage — a class of external system (GitHub, Slack, Arxiv, …) that units can bind to. Read [Connectors](../../concepts/connectors.md) for the model and [Tools](../../concepts/tools.md) for how a connector's actions surface to agents at runtime; this page is the authoring how-to.

## What you implement

A connector is a small adapter that satisfies `IConnectorType` (from `Cvoya.Spring.Connectors.Abstractions`) and registers an `ISkillRegistry` for the tools it exposes. The platform discovers connectors through DI: each connector ships its own `AddCvoyaSpringConnector<Name>()` extension (e.g. `AddCvoyaSpringConnectorGitHub`), and a host that calls it makes the connector visible across the catalog, the portal, and the CLI without further wiring.

```csharp
public sealed class AcmeConnectorType : IConnectorType
{
    public Guid TypeId => new("…");          // stable id; never changes
    public string Slug => "acme";            // URL-safe, human-readable
    public string ToolNamespace => Slug;     // default — matches Slug
    public string DisplayName => "Acme";
    public string Description => "Bridges the Acme work-tracking system.";
    public Type ConfigType => typeof(AcmeUnitConfig);

    public void MapRoutes(IEndpointRouteBuilder group) { /* GET/PUT config, actions, schema */ }

    public Task OnUnitStartingAsync(string unitId, CancellationToken ct = default) => Task.CompletedTask;
    public Task OnUnitStoppingAsync(string unitId, CancellationToken ct = default) => Task.CompletedTask;
}
```

The shape and the lifecycle hooks (`OnUnitStartingAsync` / `OnUnitStoppingAsync`, `ValidateCredentialAsync`, `VerifyContainerBaselineAsync`) are documented on the `IConnectorType` interface itself; this page focuses on the tool wiring.

## Tools live under a namespace

Every tool a connector exposes through its `ISkillRegistry` uses an id of the form `<ToolNamespace>.<tool_name>` — lowercase dotted snake_case, validated against `ToolNaming.Pattern` at registration. The platform's structural regression test enumerates every DI-registered `ISkillRegistry` and re-checks the contract from the outside, so a mis-namespaced tool fails the test suite rather than reaching production.

`IConnectorType.ToolNamespace` defaults to `Slug`. Most connectors leave it alone — the Arxiv connector ships `arxiv.search`, `arxiv.fetch_abstract`; web-search ships `websearch.query`, `websearch.summarize`. A connector that needs a different namespace (e.g. a bundle of legacy tools imported under a non-slug prefix) overrides the property explicitly:

```csharp
public string ToolNamespace => "legacy_acme";
```

The override changes the tool ids and the auto-grant rows; it does not change the connector's slug, its URL paths, or its config-store identity.

## Auto-grant on bind, revoke on unbind

The grant pipeline is data-driven. When a unit binds the connector — through `spring connector bind`, the portal's Connector tab, or the create-unit wizard — the binding write path injects one row per `<ToolNamespace>.*` tool into `unit_tool_grants` with `provenance = "connector:<Slug>"`. Unbinding revokes those rows. Re-binding swaps cleanly: the old rows go, the new rows land, and the resolver's view of the unit's effective tool set updates without operator intervention.

The implication for connector authors: **there is no per-tool grant code to write.** Registering your `ISkillRegistry` with the correct namespace is enough. The platform handles the grant rows, the resolver pulls them into the effective tool set, and agents in the bound unit (and its descendants) see the tools on their next dispatch.

Two consequences worth knowing:

- **Adding a tool is an additive change.** Shipping a new `acme.export_report` tool means: register it in the existing `ISkillRegistry`, and every unit already bound to `acme` picks it up on the next resolver call. No migration, no re-bind.
- **Removing a tool is observable.** If a tool disappears from the registry, the resolver simply stops surfacing it. Agents that were already reasoning about it lose the surface; consider deprecating with a stub registration before removing.

## Where the platform takes over

Once the registry is in place:

- **The catalog walks DI.** `GET /api/v1/connectors`, `spring connector catalog`, and the portal's `/connectors` view all hydrate from the registered `IConnectorType` set.
- **The grant resolver merges your tools into every effective tool set.** `IToolGrantResolver` reads the auto-grant rows for any unit bound to your connector and surfaces each tool with the right provenance. Agents in the bound unit see the tools alongside `sv.*` (always implicit) and any image-tier tools on the agent's container.
- **The portal surfaces them.** A unit's Config → Tools sub-tab renders one **Connectors** section per bound connector, with an inherited-from-unit badge when the grant flowed through a parent unit.

The connector author's responsibility ends at "register the right `ISkillRegistry` under the right namespace" — everything downstream is platform machinery.

## When NOT to ship a platform-MCP tool surface

Not every connector needs an `ISkillRegistry`. A connector that mainly
exists to translate inbound events and deliver per-launch credentials
into the agent container can skip the tool surface entirely — agents
talk to the external system through CLIs (`gh`, `git`, `kubectl`,
`gcloud`, …) baked into the container image and authenticated by the
short-lived secrets the
[`IConnectorRuntimeContextContributor`](../../architecture/agent-runtime.md#4g-connector-runtime-context-contribution-2380)
ships per launch.

The built-in GitHub connector follows this pattern in v0.1 (issues
[#2384](https://github.com/cvoya-com/spring-voyage/issues/2384) and
[#2383](https://github.com/cvoya-com/spring-voyage/issues/2383)). It
registers no `ISkillRegistry`, so `tools/list` returns zero `github.*`
entries for a unit with a GitHub binding. Agents use the in-container
`gh` and `git` CLIs against the credentials in
`SPRING_CONNECTOR_GITHUB_TOKEN` / `SPRING_CONNECTOR_GITHUB_OWNER` /
`SPRING_CONNECTOR_GITHUB_REPO` / `SPRING_CONNECTOR_GITHUB_REVIEWER` /
`SPRING_CONNECTOR_GITHUB_INSTALLATION_ID` /
`SPRING_CONNECTOR_GITHUB_TOKEN_EXPIRES_AT` plus the
`.spring/connectors/github/binding.json` context file (workspace-relative;
under the `.spring/` namespace per ADR-0058).

Choosing this pattern is appropriate when:

- A well-supported CLI for the external system already exists, and the
  agent's container can include it.
- Authoring is faster against the CLI than against a typed MCP tool
  surface (every `gh` flag versus a hand-rolled tool per verb).
- The platform host gains no value from intermediating each call (no
  per-tool policy, no cross-call caching that's actually useful, no
  unit-scoped budget enforcement).

Choosing a platform-MCP `ISkillRegistry` is appropriate when:

- The external system doesn't have a usable CLI inside containers.
- Per-call policy or cross-call platform smarts (caching, fan-in /
  fan-out) are load-bearing.
- The tool surface is small enough that hand-authoring per-verb
  schemas is cheaper than carrying a CLI dependency.

Either choice composes with the binding lifecycle and the
runtime-context contributor — the `ISkillRegistry` is optional, not
required.

## Things to watch

- **Slug stability.** Once a connector is in the wild, the slug is what every operator binding's `provenance = "connector:<slug>"` row points at. Renaming a slug invalidates existing grants on tenants that have the connector bound. The `TypeId` is the stable identity for *bindings*; the slug is human-readable and treated as stable for *grants*.
- **Schema discipline.** Your tool's `InputSchema` is the contract the agent's runtime validates against before calling the handler. Be precise — required fields, enum constraints, length limits — so a malformed agent invocation fails before it reaches your code.
- **No tools across namespaces.** A connector registers tools only under its own `ToolNamespace`. Cross-namespace registrations would break the auto-grant pipeline; the test suite rejects them.

## See also

- [Connectors](../../concepts/connectors.md) — the model: what a connector is and how it bridges an external system into a unit.
- [Tools](../../concepts/tools.md) — the three-tier effective tool model the platform resolves at dispatch.
- [Connectors — Operator Guide](../operator/connectors.md) — installing and binding a connector at runtime.
- [Architecture — Connectors](../../architecture/connectors.md) — implementation tiers, route conventions, built-in connector inventory.
