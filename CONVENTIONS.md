# Spring Voyage — Coding Conventions

These conventions ensure code from parallel agents merges cleanly. All agents (Claude Code, Cursor, GitHub Copilot) follow these rules.

## Architecture reference

- **Architecture index:** [`docs/architecture/README.md`](docs/architecture/README.md) — canonical entry point for platform architecture documents.
- **Decision records:** [`docs/decisions/README.md`](docs/decisions/README.md) — the "why" behind major design choices.
- **Namespace root:** `Cvoya.Spring.*`
- **Target framework:** .NET 10
- **Runtime:** Dapr sidecar pattern

## 0. File Layout

### Copyright header

Required on all C# source files:

```csharp
// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.
```

### Namespace and using order

File-scoped namespaces. Namespace declaration immediately after the copyright header; `using` statements after:

```csharp
// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Messaging;

using System;
using Microsoft.Extensions.Logging;
```

- File-scoped namespaces (no braces).
- Namespace immediately after the copyright header.
- `using` statements after the namespace declaration.

## 1. Project Structure

- Namespace matches folder path: `Cvoya.Spring.Core.Messaging` lives in `src/Cvoya.Spring.Core/Messaging/`.
- One public type per file. File name matches type name (`AgentActor.cs`, `IMessageReceiver.cs`).
- Internal/private helper types may share a file with the public type they support.
- `Cvoya.Spring.Core` has zero external NuGet package references — domain abstractions only.

## 2. Naming

| Element | Convention | Example |
|---|---|---|
| Files | PascalCase, match type name | `AgentActor.cs`, `IMessageReceiver.cs` |
| Interfaces | `I`-prefixed | `IAddressable`, `IOrchestrationStrategy` |
| Abstract classes | No prefix | `ActorBase`, `ConnectorBase` |
| Records (immutable data) | PascalCase noun | `Message`, `Address`, `ActivityEvent` |
| Enums | PascalCase, singular | `MessageType`, `ExecutionMode` |
| Test classes | `{Class}Tests` | `AgentActorTests`, `MessageRouterTests` |
| Test methods | `MethodName_Scenario_ExpectedResult` | `ReceiveAsync_CancelMessage_CancelsActiveWork` |
| Constants | PascalCase | `StateKeys.ActiveConversation` |
| Private fields | `_camelCase` | `_stateManager`, `_logger` |

### Identifiers

Every actor (unit, agent, human, connector, tenant) has exactly one stable identifier: a `Guid`. There is no parallel string identifier (no slug, no scoped handle, no namespaced name). `display_name` is presentation-only — never unique, never addressable, never a foreign-key target. See [`docs/architecture/identifiers.md`](docs/architecture/identifiers.md) and [ADR-0036](docs/decisions/0036-single-identity-model.md) for the durable decision.

- **Type.** Repository signatures, DTO ids, route parameters, and method parameters that take an actor identifier are typed `Guid`. Never `string`.
- **Wire form on URLs, address strings, manifest references, CLI output, log lines.** 32-char lowercase no-dash hex (`Guid.ToString("N")`). One helper: `Cvoya.Spring.Core.Identifiers.GuidFormatter.Format`.
- **Wire form in JSON DTO bodies.** Standard dashed `8-4-4-4-12`. Kiota's `GetGuidValue()` and STJ's default `Utf8JsonReader.GetGuid()` accept the dashed form natively; the OSS host registers `Cvoya.Spring.Host.Api.Serialization.NoDashGuidJsonConverter` so the no-dash form deserialises too.
- **Parse is lenient on every surface.** `GuidFormatter.TryParse`, `Address.TryParse`, ASP.NET Core's `{id:guid}` route binder, the `NoDashGuidJsonConverter`, and the CLI's `CliResolver.TryParseGuid` all accept both no-dash and dashed forms (and any other shape `Guid.TryParse` recognises). Emit asymmetry — emit one form per surface, parse many — keeps copy-paste workflows working.
- **`Address` shape.** `Address` is a record with `Scheme` (string) and `Id` (`Guid`). The canonical render is `scheme:<32-hex-no-dash>` (e.g. `agent:8c5fab2a8e7e4b9c92f1d8a3b4c5d6e7`). There is no path form, no `@<uuid>` form. Use the scheme constants on `Address` (`AgentScheme`, `UnitScheme`, `HumanScheme`).
- **`display_name`.** Validated by `Cvoya.Spring.Core.Validation.DisplayNameValidator` on every write surface; values that round-trip through `Guid.TryParseExact` for any standard form are rejected with structured `code = display_name_is_guid_shape`. CLI verbs accept `display_name` only as **search input** (`spring agent show <id-or-name>` short-circuits to a direct lookup when the argument parses as a Guid; otherwise it runs a name search returning 0/1/n).

## 3. Error Handling

```csharp
public class SpringException : Exception
{
    public SpringException(string message) : base(message) { }
    public SpringException(string message, Exception inner) : base(message, inner) { }
}

public class EntityNotFoundException : SpringException { ... }
public class PermissionDeniedException : SpringException { ... }
public class InvalidAddressException : SpringException { ... }
```

**Result type for expected failures** (e.g., message routing):

```csharp
public readonly record struct Result<TValue, TError>
{
    public TValue? Value { get; }
    public TError? Error { get; }
    public bool IsSuccess { get; }

    public static Result<TValue, TError> Success(TValue value) => ...;
    public static Result<TValue, TError> Failure(TError error) => ...;
}
```

**Rules:**

- Actor methods must NEVER let exceptions escape the actor turn. Catch, log, update state, return error response.
- Use `Result<T, TError>` for operations that fail in expected ways (routing, resolution).
- Use exceptions for unexpected/infrastructure failures (DB down, serialisation error).
- Always log exceptions with structured data before swallowing.

## 4. Dependency Injection

Each project provides an extension method:

```csharp
public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddCvoyaSpringCore(this IServiceCollection services) { ... }
    public static IServiceCollection AddCvoyaSpringDapr(this IServiceCollection services) { ... }
}
```

**Rules:**

- Constructor injection only. No service locator, no `IServiceProvider` injection.
- Prefer **primary constructors**:

  ```csharp
  public class MessageRouter(
      IDirectoryService directory,
      ILogger<MessageRouter> logger) : IMessageRouter
  {
      public async Task RouteAsync(Message message, CancellationToken ct)
      {
          logger.LogInformation("Routing message {MessageId}", message.Id);
          // ...
      }
  }
  ```

- Keyed services for strategy patterns:

  ```csharp
  services.AddKeyedSingleton<IOrchestrationStrategy, AiOrchestrationStrategy>("ai");
  services.AddKeyedSingleton<IOrchestrationStrategy, WorkflowOrchestrationStrategy>("workflow");
  ```

- **Use `TryAdd*` for all service registrations** so downstream consumers can override implementations by registering their own before calling `AddCvoyaSpring*()`:

  ```csharp
  public static IServiceCollection AddCvoyaSpringDapr(this IServiceCollection services)
  {
      services.TryAddSingleton<IMessageRouter, MessageRouter>();
      services.TryAddScoped<IDirectoryService, DaprDirectoryService>();
      return services;
  }
  ```

- Registration in `Program.cs`:

  ```csharp
  builder.Services
      .AddCvoyaSpringCore()
      .AddCvoyaSpringDapr()
      .AddCvoyaSpringConnectorGitHub();
  ```

## 5. Dapr Patterns

**State keys** — centralised constants prevent typos across parallel work:

```csharp
public static class StateKeys
{
    // AgentActor state
    public const string ActiveConversation = "Agent:ActiveConversation";
    public const string PendingConversations = "Agent:PendingConversations";
    public const string ObservationChannel = "Agent:ObservationChannel";
    public const string AgentDefinition = "Agent:Definition";
    public const string InitiativeState = "Agent:InitiativeState";

    // UnitActor state
    public const string Members = "Unit:Members";
    public const string Policies = "Unit:Policies";
    public const string DirectoryCache = "Unit:DirectoryCache";
    public const string UnitDefinition = "Unit:Definition";
}
```

**Pub/sub topic naming:** `{tenant-id}/{owner-id}/{topic}`

- Both `{tenant-id}` and `{owner-id}` are 32-char no-dash hex Guids. The owner is the unit (or other addressable) that anchors the topic; the canonical wire form is what `GuidFormatter.Format` emits.
- Example: `dd55c4ea8d725e43a9df88d07af02b69/8c5fab2a8e7e4b9c92f1d8a3b4c5d6e7/pr-reviews`
- System topics use the literal `system/` prefix: `system/directory-changed`, `system/activity`.

**All Dapr interactions** go through `Cvoya.Spring.Core` abstractions, implemented in `Cvoya.Spring.Dapr`. No direct Dapr SDK calls from actors — actors use injected interfaces.

## 6. Testing

**Stack:** xUnit + FluentAssertions + NSubstitute.

```csharp
public abstract class ActorTestBase<TActor> where TActor : class
{
    protected readonly IActorStateManager StateManager = Substitute.For<IActorStateManager>();
    protected readonly ILogger<TActor> Logger = Substitute.For<ILogger<TActor>>();

    protected Message CreateMessage(
        MessageType type = MessageType.Domain,
        string? threadId = null,
        JsonElement? payload = null)
    {
        return new Message
        {
            Id = Guid.NewGuid(),
            From = new Address(Address.AgentScheme, Guid.NewGuid()),
            To = new Address(Address.AgentScheme, Guid.NewGuid()),
            Type = type,
            ThreadId = threadId ?? Guid.NewGuid().ToString(),
            Payload = payload ?? default,
            Timestamp = DateTimeOffset.UtcNow
        };
    }
}
```

**Test naming:** `MethodName_Scenario_ExpectedResult`.

```csharp
public class AgentActorTests : ActorTestBase<AgentActor>
{
    [Fact]
    public async Task ReceiveAsync_DomainMessageNewConversation_CreatesConversationChannel() { ... }

    [Fact]
    public async Task ReceiveAsync_CancelMessage_CancelsActiveWork() { ... }
}
```

**Integration tests:** Testcontainers for PostgreSQL. Dapr test mode for actor tests.

**Rules:**

- Every public method has at least one test.
- Test the behaviour, not the implementation.
- Use `ITestOutputHelper` for diagnostic output.
- No `Thread.Sleep` — use `Task.Delay` or test synchronisation primitives.

## 7. Async

- `Async` suffix on all async methods: `ReceiveAsync`, `ResolveAddressAsync`.
- `CancellationToken` as the last parameter on all public async methods.
- Never block on async: no `.Result`, no `.Wait()`, no `.GetAwaiter().GetResult()`.
- Use `ValueTask` for hot paths that often complete synchronously.

## 8. Serialization

**System.Text.Json only.** No Newtonsoft.Json anywhere.

```csharp
[JsonSerializable(typeof(Message))]
[JsonSerializable(typeof(Address))]
[JsonSerializable(typeof(ActivityEvent))]
internal partial class SpringCoreJsonContext : JsonSerializerContext { }
```

**Rules:**

- All serialisable types are records or have parameterless constructors.
- Use `[JsonPropertyName("camelCase")]` for external APIs.
- Internal serialisation (Dapr state) uses PascalCase (default).
- `JsonElement` for untyped payloads — not `object` or `dynamic`.
- **Enums that cross actor-remoting or HTTP MUST serialize by name.** Register `JsonStringEnumConverter(allowIntegerValues: false)` on any `JsonSerializerOptions` used at those boundaries. Mid-enum insertion is safe once this is enforced — without it, always append. The `allowIntegerValues: false` setting ensures that a misbehaving caller sending an ordinal receives a deterministic deserialization failure rather than silently landing on an adjacent enum value. See `ActorRemotingJsonOptions` (actor-remoting) and `Program.cs` (HTTP) for the canonical registrations (#956).

## 9. Logging

**`ILogger<T>` via constructor injection.** Structured logging with event IDs.

**Event ID ranges per project:**

| Project | Range | Example |
|---|---|---|
| Cvoya.Spring.Core | 1000–1999 | 1001: MessageCreated |
| Cvoya.Spring.Dapr.Actors | 2000–2099 | 2001: ActorActivated |
| Cvoya.Spring.Dapr.Routing | 2100–2199 | 2101: AddressResolved |
| Cvoya.Spring.Dapr.Execution | 2200–2299 | 2201: ExecutionDispatched |
| Cvoya.Spring.Host.Api | 3000–3999 | 3001: RequestReceived |
| Cvoya.Spring.Cli | 4000–4999 | 4001: CommandExecuted |
| Cvoya.Spring.Connector.GitHub | 5000–5999 | 5001: WebhookReceived |

```csharp
public static partial class LogMessages
{
    [LoggerMessage(EventId = 2001, Level = LogLevel.Information, Message = "Actor {ActorType}:{ActorId} activated")]
    public static partial void ActorActivated(this ILogger logger, string actorType, string actorId);
}
```

## 10. Message Handling Pattern

All actors follow the same `ReceiveAsync` dispatch pattern:

```csharp
public async Task<Message?> ReceiveAsync(Message message)
{
    return message.Type switch
    {
        MessageType.Cancel => await HandleCancelAsync(message),
        MessageType.StatusQuery => HandleStatusQuery(message),
        MessageType.HealthCheck => HandleHealthCheck(message),
        MessageType.PolicyUpdate => await HandlePolicyUpdateAsync(message),
        MessageType.Domain => await HandleDomainMessageAsync(message),
        _ => throw new SpringException($"Unknown message type: {message.Type}")
    };
}
```

Control messages (Cancel, StatusQuery, HealthCheck, PolicyUpdate) have platform-defined behaviour. Domain messages route to the actor's domain logic (mailbox for agents, strategy for units).

## 11. Build Configuration

`Directory.Build.props` (solution-wide):

```xml
<Project>
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
  </PropertyGroup>
</Project>
```

Central package management via `Directory.Packages.props` — all NuGet versions pinned centrally.

## 12. Extensibility — Tenancy

General extensibility rules (`TryAdd*`, no-seal, visibility, virtual hooks, no tenant assumptions, no statics) live in [`AGENTS.md`](AGENTS.md) § "Open-source platform and extensibility". The conventions below are the tenancy-specific rules that belong with code patterns.

**Multi-tenancy (business-data entities):** every new business-data entity implements `Cvoya.Spring.Core.Tenancy.ITenantScopedEntity` with a `Guid TenantId` column, and its `IEntityTypeConfiguration` adds the combined tenant + soft-delete query filter — `HasQueryFilter(e => e.TenantId == tenantContext.CurrentTenantId && e.DeletedAt == null)` — dropping the soft-delete clause only for entities without a `DeletedAt` column. The DbContext auto-populates `TenantId` from the injected `ITenantContext` on insert; write sites do not set it explicitly. The OSS deployment runs functionally single-tenant; every fresh-install row is owned by `Cvoya.Spring.Core.Tenancy.OssTenantIds.Default` (the deterministic v5 UUID `dd55c4ea-8d72-5e43-a9df-88d07af02b69`; `OssTenantIds.DefaultDashed` and `OssTenantIds.DefaultNoDash` expose the literal string forms for configs, dashboards, and audit-log greps). Never hardcode a string `"default"` — the tenant id is a `Guid`. System/ops tables (migrations history, startup config) stay global.

**Cross-tenant reads and writes go through `ITenantScopeBypass`.** The EF Core query filter restricts reads and writes to the current tenant. A small set of operations legitimately need to cross that boundary — `DatabaseMigrator`, platform-wide analytics, system administration. Those call sites wrap the work in `ITenantScopeBypass.BeginBypass(reason)` so the bypass is auditable (structured log on open and close, with caller context and duration) and so the cloud overlay can swap the default for a permission-checked variant. Never call `IgnoreQueryFilters()` directly in business code.

**Bootstrap seeds via `ITenantSeedProvider`; implementations must be idempotent and must not overwrite user edits.** The default-tenant bootstrap hosted service iterates every DI-registered `ITenantSeedProvider` in ascending `Priority` order on host startup, gated by `Tenancy:BootstrapDefaultTenant` (default true). Implementations upsert by `(tenant_id, <natural-key>)`, log every action at `Information`, and treat seed values as initial data — operator edits made after the seed always win.

## 13. UI / CLI Feature Parity

Every user-facing feature ships through BOTH the web portal UI and the `spring` CLI. Neither surface drifts ahead of the other.

- When planning a feature PR, enumerate the affected surfaces (API endpoints, UI screens, CLI commands). If a surface is missing, either include it in the same PR or file a sibling issue before the PR lands so the gap is tracked.
- "The UI can do X but the CLI can't" (or vice versa) is a real bug, not a speculative nice-to-have.
- Link CLI-side and UI-side issues as siblings when a feature is split across PRs.
- A CLI scenario under `tests/cli-scenarios/` is a good parity proxy: if the scenario has to fall back to `curl` because the CLI lacks the command, the CLI is behind.

**Exceptions:** admin/ops operations that are genuinely dev-only (e.g., `dotnet ef migrations add`) don't need a UI counterpart. Internal test affordances are also out of scope.

**Operator carve-out:** operational surfaces (agent-runtime config, connector config, credential health, tenant seeds, skill-bundle bindings) are CLI-only by design. The portal MAY expose read-only views; mutations go through the CLI. See [`AGENTS.md`](AGENTS.md) § "Operator surfaces".

## 14. Skill-Bundle Tenant Binding

Tenants see only skill bundles bound to them. Discovery stays filesystem-based — `FileSystemSkillBundleResolver` walks the packages root — but `TenantFilteringSkillBundleResolver` wraps it and checks the current tenant's `ITenantSkillBundleBindingService` for an `enabled=true` row before delegating. Unbound or disabled bundles surface as `SkillBundlePackageNotFoundException`, indistinguishable from a missing package so callers never leak the existence of bundles they can't use.

- Bootstrap populates default-tenant bindings from the on-disk packages layout (via `FileSystemSkillBundleSeedProvider`). The Worker host runs bootstrap at startup; the API host reads the bindings.
- A manifest entry like `spring-voyage/software-engineering` looks up the binding keyed on the package directory name `software-engineering`. Prefix normalisation lives in both the inner resolver and the decorator — they must not diverge.

## 15. Credential-Health Watchdog

Every `HttpClient` used by an agent runtime or connector that authenticates against a remote service flows through the `CredentialHealthWatchdogHandler`. Without it, revoked or expired tokens surface only when a unit fails at run-time — the operator sees no accumulating signal.

Wiring pattern (inside a runtime/connector's `AddCvoya…()` DI extension):

```csharp
services.AddHttpClient("my-runtime-client")
    .AddCredentialHealthWatchdog(
        kind: CredentialHealthKind.AgentRuntime,
        subjectId: "my-runtime",
        secretName: "api-key");
```

- `subjectId` is the runtime `Id` (for `CredentialHealthKind.AgentRuntime`) or connector `Slug` (for `CredentialHealthKind.Connector`).
- `secretName` is the credential key — `"api-key"` for single-credential subjects; stable per-credential names for multi-part auth.
- The handler flips the persistent credential-health row on `401` (→ `Invalid`) and `403` (→ `Revoked`); other status codes pass through unmodified so a flaky upstream does not flap operator-facing status.
- Handler writes go through a child DI scope — safe to use from any pipeline, including background hosted services with no ambient request scope.

## 16. Agent Runtimes and Connectors Are Plugins

Every agent runtime (`IAgentRuntime`) and connector (`IConnectorType`) is a first-class extension point. The host references the abstraction only; concrete implementations live in their own project and register via DI.

### Project layout

- **Agent runtimes** live under `src/Cvoya.Spring.AgentRuntimes.<Name>/` and reference `Cvoya.Spring.Core` only. Each project ships:
  - A single `AddCvoyaSpringAgentRuntime<Name>()` DI extension, registered with `TryAddEnumerable(ServiceDescriptor.Singleton<IAgentRuntime, …>)` so a cloud overlay can pre-register a variant without displacing the OSS default.
  - A `seed.json` at `agent-runtimes/<id>/seed.json` carrying the runtime's `DefaultModels` catalogue.
  - A per-project `README.md` documenting the runtime's id, tool kind, credential schema, and any host-side baseline tooling.
- **Connectors** live under `src/Cvoya.Spring.Connector.<Name>/` and reference `Cvoya.Spring.Connectors.Abstractions`. Each connector exposes `AddCvoyaSpringConnector<Name>(IConfiguration configuration)` and registers its `IConnectorType` as a singleton. Connector-specific HTTP routes attach via the `MapRoutes(IEndpointRouteBuilder group)` contract — the host calls it on a pre-scoped `/api/v1/connectors/{slug}` group so the connector package stays ignorant of the outer path shape.

### Tenant install surfaces

Both plugin kinds sit behind a **tenant install table** (`tenant_agent_runtime_installs`, `tenant_connector_installs`) managed by `ITenantAgentRuntimeInstallService` / `ITenantConnectorInstallService`. A plugin registered in DI is *available* to the host; an install row makes it *visible* to a given tenant. Bootstrap seeds default-tenant installs for every registered plugin; subsequent lifecycle goes through the install service.

### Credential-health wiring

Plugins that authenticate via `HttpClient` MUST wire `AddCredentialHealthWatchdog(kind, subjectId, secretName)` onto their named client (see § 15). For agent runtimes, credential probing runs inside the unit's chosen container via the `UnitValidationWorkflow` — runtimes expose `GetProbeSteps(config, credential)` and the workflow dispatches probes per execution image. For connectors, the accept-time path remains `POST /validate-credential` → `IConnectorType.ValidateCredentialAsync`.

### Adding a new plugin

1. Create `src/Cvoya.Spring.<Kind>.<Name>/` with a single DI-extension entry point. Reference `Cvoya.Spring.Core` (runtimes) or `Cvoya.Spring.Connectors.Abstractions` (connectors) only.
2. Implement the contract; wire the credential-health watchdog on any HttpClient that authenticates.
3. For runtimes, ship a `seed.json`. For connectors, document the typed routes exposed via `MapRoutes` in the project `README.md`.
4. Register the DI extension from `Program.cs` in the host. No changes to the dispatcher project are required — the install surface, registry, and bootstrap pick up the new plugin automatically.

## 17. Documentation

### Docs describe shipped behaviour

`docs/concepts/`, `docs/guide/`, `docs/architecture/`, top-level `README.md`, and every `packages/*/README.md` describe the system as it exists in the current codebase. Every "this works" / "this exists" / "this returns" claim corresponds to a verifiable surface — a function, an endpoint, a CLI verb, a YAML key — that a reviewer can grep for.

The existing `docs-evergreen-framing` CI gate enforces that `docs/` never references outdated version labels (`V2`, `V2.1`). This convention operates at a higher level: **content accuracy**, not version tagging.

### Aspirational content lives in `docs/plan/` or under a Planned callout

Planned features, deferred work, and "we will eventually" framing belong in `docs/plan/<release>/` (the per-release plan-of-record narrative). When aspirational content must appear in an in-place doc — e.g. a concept doc explaining the long-term shape — it uses a clearly-marked callout:

> **Planned (v0.2):** … or … **Not yet implemented:** …

The callout names the release or links the tracking issue. Bare "we plan to" prose without the callout is the failure mode this rule catches.

### PR review verifies it

When a PR touches `docs/concepts/`, `docs/guide/`, `docs/architecture/`, top-level `README.md`, or `packages/*/README.md`, reviewers must verify:

1. Every behavioural claim still matches an identifiable surface in the current codebase.
2. Aspirational content uses the **Planned** callout described above — not bare future-tense prose.
