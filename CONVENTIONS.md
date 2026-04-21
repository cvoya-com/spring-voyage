# Spring Voyage V2 — Coding Conventions

These conventions ensure that code from parallel agents merges cleanly. All agents (Claude Code, Cursor, GitHub Copilot) must follow these rules.

## Architecture Reference

- **Architecture index:** [`docs/architecture/README.md`](docs/architecture/README.md) — the canonical entry point for the platform's architecture documents
- **Decision records:** [`docs/decisions/README.md`](docs/decisions/README.md) — the "why" behind the major design choices
- **Namespace root:** `Cvoya.Spring.*`
- **Target framework:** .NET 10
- **Runtime:** Dapr sidecar pattern

## 0. File Layout

### Copyright Header

Required on all C# source files:

```csharp
// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.
```

### Namespace and Using Order

File-scoped namespaces. Namespace declaration immediately after the copyright header, `using` statements after:

```csharp
// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Messaging;

using System;
using Microsoft.Extensions.Logging;
```

- File-scoped namespaces (no braces)
- Namespace immediately after copyright notice
- `using` statements after namespace declaration

## 1. Project Structure Rules
- Namespace matches folder path: `Cvoya.Spring.Core.Messaging` lives in `src/Cvoya.Spring.Core/Messaging/`
- One public type per file. File name matches type name (e.g., `AgentActor.cs`)
- Internal/private helper types may share a file with the public type they support
- `Cvoya.Spring.Core` must have ZERO external package references (no NuGet packages). It defines domain abstractions only.

## 2. Naming

| Element | Convention | Example |
|---------|-----------|---------|
| Files | PascalCase, match type name | `AgentActor.cs`, `IMessageReceiver.cs` |
| Interfaces | `I`-prefixed | `IAddressable`, `IOrchestrationStrategy` |
| Abstract classes | No prefix | `ActorBase`, `ConnectorBase` |
| Records (immutable data) | PascalCase noun | `Message`, `Address`, `ActivityEvent` |
| Enums | PascalCase, singular | `MessageType`, `ExecutionMode` |
| Test classes | `{Class}Tests` | `AgentActorTests`, `MessageRouterTests` |
| Test methods | `MethodName_Scenario_ExpectedResult` | `ReceiveAsync_CancelMessage_CancelsActiveWork` |
| Constants | PascalCase | `StateKeys.ActiveConversation` |
| Private fields | `_camelCase` | `_stateManager`, `_logger` |

## 3. Error Handling

```csharp
// Base exception — all domain exceptions inherit from this
public class SpringException : Exception
{
    public SpringException(string message) : base(message) { }
    public SpringException(string message, Exception inner) : base(message, inner) { }
}

// Specific exceptions
public class EntityNotFoundException : SpringException { ... }
public class PermissionDeniedException : SpringException { ... }
public class InvalidAddressException : SpringException { ... }
```

**Result type for expected failures** (e.g., message routing):

```csharp
// In Cvoya.Spring.Core
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
- Use `Result<T, TError>` for operations that can fail in expected ways (routing, resolution)
- Use exceptions for unexpected/infrastructure failures (DB down, serialization error)
- Always log exceptions with structured data before swallowing

## 4. Dependency Injection

```csharp
// Each project provides an extension method
public static class ServiceCollectionExtensions
{
    // In Cvoya.Spring.Core — register core abstractions
    public static IServiceCollection AddCvoyaSpringCore(this IServiceCollection services) { ... }

    // In Cvoya.Spring.Dapr — register Dapr implementations
    public static IServiceCollection AddCvoyaSpringDapr(this IServiceCollection services) { ... }
}
```

**Rules:**
- Constructor injection only. No service locator, no `IServiceProvider` injection.
- Prefer **primary constructors** for dependency injection:
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
- Keyed services for strategy pattern:
  ```csharp
  services.AddKeyedSingleton<IOrchestrationStrategy, AiOrchestrationStrategy>("ai");
  services.AddKeyedSingleton<IOrchestrationStrategy, WorkflowOrchestrationStrategy>("workflow");
  ```
- **Use `TryAdd*` for all service registrations** so downstream consumers (e.g., the private cloud repo) can override implementations by registering their own before calling `AddCvoyaSpring*()`:
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

**State keys** — centralized constants to prevent typos across parallel work:

```csharp
// In Cvoya.Spring.Dapr
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

**Pub/sub topic naming:** `{tenant}/{unit-path}/{topic}`
- Example: `acme/engineering-team/pr-reviews`
- System topics: `system/directory-changed`, `system/activity`

**All Dapr interactions** go through `Cvoya.Spring.Core` abstractions, implemented in `Cvoya.Spring.Dapr`. No direct Dapr SDK calls from actors — actors use injected interfaces.

## 6. Testing

**Stack:** xUnit + FluentAssertions + NSubstitute

```csharp
// Test base class for actor tests
public abstract class ActorTestBase<TActor> where TActor : class
{
    protected readonly IActorStateManager StateManager = Substitute.For<IActorStateManager>();
    protected readonly ILogger<TActor> Logger = Substitute.For<ILogger<TActor>>();

    // Helper: create a test message
    protected Message CreateMessage(
        MessageType type = MessageType.Domain,
        string? conversationId = null,
        JsonElement? payload = null)
    {
        return new Message
        {
            Id = Guid.NewGuid(),
            From = new Address("agent", "test-sender"),
            To = new Address("agent", "test-receiver"),
            Type = type,
            ConversationId = conversationId ?? Guid.NewGuid().ToString(),
            Payload = payload ?? default,
            Timestamp = DateTimeOffset.UtcNow
        };
    }
}
```

**Test naming:** `MethodName_Scenario_ExpectedResult`

```csharp
public class AgentActorTests : ActorTestBase<AgentActor>
{
    [Fact]
    public async Task ReceiveAsync_DomainMessageNewConversation_CreatesConversationChannel() { ... }

    [Fact]
    public async Task ReceiveAsync_CancelMessage_CancelsActiveWork() { ... }

    [Fact]
    public async Task ReceiveAsync_DomainMessageExistingConversation_RoutesToChannel() { ... }
}
```

**Integration tests:** Use Testcontainers for PostgreSQL. Use Dapr test mode for actor tests.

**Rules:**
- Every public method needs at least one test
- Test the behavior, not the implementation
- Use `ITestOutputHelper` for diagnostic output
- No `Thread.Sleep` — use `Task.Delay` or test synchronization primitives

## 7. Async

- `Async` suffix on all async methods: `ReceiveAsync`, `ResolveAddressAsync`
- `CancellationToken` as last parameter on all public async methods
- Never block on async: no `.Result`, no `.Wait()`, no `.GetAwaiter().GetResult()`
- Use `ValueTask` for hot paths that often complete synchronously

## 8. Serialization

**System.Text.Json only.** No Newtonsoft.Json anywhere.

```csharp
// Source-generated context per project for AOT compatibility
[JsonSerializable(typeof(Message))]
[JsonSerializable(typeof(Address))]
[JsonSerializable(typeof(ActivityEvent))]
internal partial class SpringCoreJsonContext : JsonSerializerContext { }
```

**Rules:**
- All serializable types must be records or have parameterless constructors
- Use `[JsonPropertyName("camelCase")]` for external APIs
- Internal serialization (Dapr state) uses PascalCase (default)
- `JsonElement` for untyped payloads (not `object` or `dynamic`)

## 9. Logging

**`ILogger<T>` via constructor injection.** Structured logging with event IDs.

**Event ID ranges per project:**

| Project | Range | Example |
|---------|-------|---------|
| Cvoya.Spring.Core | 1000-1999 | 1001: MessageCreated |
| Cvoya.Spring.Dapr.Actors | 2000-2099 | 2001: ActorActivated |
| Cvoya.Spring.Dapr.Routing | 2100-2199 | 2101: AddressResolved |
| Cvoya.Spring.Dapr.Execution | 2200-2299 | 2201: ExecutionDispatched |
| Cvoya.Spring.Host.Api | 3000-3999 | 3001: RequestReceived |
| Cvoya.Spring.Cli | 4000-4999 | 4001: CommandExecuted |
| Cvoya.Spring.Connector.GitHub | 5000-5999 | 5001: WebhookReceived |

```csharp
// Use LoggerMessage source generators for performance
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

Control messages (Cancel, StatusQuery, HealthCheck, PolicyUpdate) have platform-defined behavior. Domain messages are routed to the actor's domain logic (mailbox for agents, strategy for units).

## 11. Parallel Agent Coordination

Multiple agents work on v2 simultaneously. Follow these rules:

- **Small, focused PRs.** One issue per PR. This reduces merge conflicts.
- **Rebase before merge.** Always `git fetch origin && git rebase origin/main` before opening or updating a PR.
- **Coordinate on shared types.** `Cvoya.Spring.Core` types are defined in Issue #3. If you need to add a type, check that no other agent is adding the same thing.
- **Append, don't reorder.** When adding to `StateKeys`, DI registrations, or enum values — append to the end.
- **Interface-first.** Define the interface in `Cvoya.Spring.Core`, implement in `Cvoya.Spring.Dapr`. This allows parallel work on different implementations.
- **File follow-up issues before the PR lands, and reference concrete numbers.** When a PR body mentions deferred work ("follow-up to come", "the X path is out of scope"), file the issue first and substitute the real `#N` into the PR body. GitHub does not auto-link prose-only references to non-existent issues, and the "we'll file it later" form routinely drops follow-ups on the floor — by the time the PR merges, the context is gone. Filing first also proves the deferral is tracked, not hand-waved.

## 12. Build Configuration

**Directory.Build.props** (solution-wide):
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

**Central package management** via `Directory.Packages.props` — all NuGet versions pinned centrally.

## 13. Extensibility Conventions

General extensibility rules (TryAdd*, no-seal, visibility, virtual hooks, no tenant assumptions, no statics) are covered in `AGENTS.md` § "Open-Source Platform & Extensibility". The conventions below are the tenancy-specific rules that belong with code patterns.

**Multi-tenancy (business-data entities):** Any new business-data entity MUST implement `Cvoya.Spring.Core.Tenancy.ITenantScopedEntity` and its `IEntityTypeConfiguration` MUST add the combined tenant + soft-delete query filter — `HasQueryFilter(e => e.TenantId == tenantContext.CurrentTenantId && e.DeletedAt == null)`, dropping the soft-delete clause only for entities that do not carry a `DeletedAt` column. The DbContext auto-populates `TenantId` from the injected `ITenantContext` on insert, so write sites do not set it explicitly. System/ops tables (migrations history, startup config) stay global and are not tenant-scoped. See issue #674 for background and the broader refactor plan.

**Cross-tenant reads and writes go through `ITenantScopeBypass`.** The EF Core query filter applied to every tenant-scoped entity restricts reads and writes to the current tenant. A small set of operations legitimately need to cross that boundary — `DatabaseMigrator`, platform-wide analytics, system administration. Those call sites wrap the work in `ITenantScopeBypass.BeginBypass(reason)` so the bypass is auditable (structured log on open and close, with caller context and duration) and so the private cloud repo can swap the default implementation for a permission-checked variant. Never call `IgnoreQueryFilters()` directly in business code — if a feature seems to need it, rethink the feature or file an issue.

**Bootstrap seeds are declared via `ITenantSeedProvider`; implementations MUST be idempotent and MUST NOT overwrite user edits.** The default-tenant bootstrap hosted service iterates every DI-registered `Cvoya.Spring.Core.Tenancy.ITenantSeedProvider` in ascending `Priority` order on host startup, gated by `Tenancy:BootstrapDefaultTenant` (default true). Implementations upsert by `(tenant_id, <natural-key>)`, log every action at `Information`, and treat seed values as initial data — operator edits made after the seed landed always win. The bootstrap runs on every startup, so a non-idempotent provider that re-inserts on each call duplicates rows and breaks the contract.

## 14. UI / CLI Feature Parity

Every user-facing feature must ship through BOTH the web portal UI and the `spring` CLI. Neither surface is allowed to drift ahead of the other.

- When planning a feature PR, enumerate the affected surfaces (API endpoints, UI screens, CLI commands). If a surface is missing, either include it in the same PR or file a sibling issue before the PR lands so the gap is tracked.
- Treat "the UI can do X but the CLI can't" (or vice versa) as a real bug, not a speculative nice-to-have.
- Link CLI-side and UI-side issues as siblings when a feature is split across PRs.
- An E2E scenario under `tests/e2e/` is a good parity proxy: if the scenario has to fall back to `curl` because the CLI lacks the command, the CLI is behind.

**Exceptions:** admin/ops operations that are genuinely dev-only (e.g., `dotnet ef migrations add`) don't need a UI counterpart. Internal test affordances are also out of scope.

**Admin/operator carve-out (OSS only, per #693 / #674).** A set of operational surfaces — agent-runtime config, connector config, credential health, tenant seeds, skill-bundle bindings — are CLI-only by design. Under the v2 IA (umbrella #815 § 2) the read-only portal views for these surfaces live inside the regular IA at `/settings/agent-runtimes` and `/connectors?tab=health`; the retired `/admin/*` top-level routes no longer exist. See [`AGENTS.md` § "Admin surfaces (CLI-only) — relaxation of the parity rule"](AGENTS.md#admin-surfaces-cli-only--relaxation-of-the-parity-rule) for the authoritative roster; the list there is the one to update when a new admin surface lands. User-facing features remain strictly parity-bound.

## 15. Skill-Bundle Tenant Binding

Tenants see only skill bundles bound to them. Discovery stays filesystem-based — `FileSystemSkillBundleResolver` walks the packages root — but `TenantFilteringSkillBundleResolver` wraps it and checks the current tenant's `ITenantSkillBundleBindingService` for an `enabled=true` row before delegating. Unbound or disabled bundles surface as `SkillBundlePackageNotFoundException`, indistinguishable from a missing package so callers never leak the existence of bundles they can't use.

- Bootstrap populates default-tenant bindings from the on-disk packages layout (via `FileSystemSkillBundleSeedProvider`). The Worker host runs bootstrap at startup; the API host reads the bindings.
- A `spring skill-bundle …` CLI to mutate bindings is V2.1 — V2 does not expose mutation over HTTP or CLI. Operator surfaces stay read-only.
- A manifest entry like `spring-voyage/software-engineering` looks up the binding keyed on the package directory name `software-engineering`. Prefix normalization lives in both the inner resolver and the decorator — they must not diverge.

## 16. Credential-Health Watchdog

Every `HttpClient` used by an agent runtime or connector that authenticates against a remote service MUST flow through the `CredentialHealthWatchdogHandler` (`src/Cvoya.Spring.Dapr/CredentialHealth/`). Without it, revoked or expired tokens surface only when a unit fails at run-time — the operator sees no accumulating signal.

Wiring pattern (inside a runtime/connector's `AddCvoya…()` DI extension):

```csharp
services.AddHttpClient("my-runtime-client")
    .AddCredentialHealthWatchdog(
        kind: CredentialHealthKind.AgentRuntime,
        subjectId: "my-runtime",
        secretName: "api-key");
```

- `subjectId` is the runtime `Id` (for `CredentialHealthKind.AgentRuntime`) or connector `Slug` (for `CredentialHealthKind.Connector`).
- `secretName` is the credential key inside the subject — use `"api-key"` for single-credential subjects and stable names per credential for multi-part auth.
- The handler flips the persistent credential-health row on `401` (→ `Invalid`) and `403` (→ `Revoked`); other status codes pass through unmodified so a flaky upstream does not flap the operator-facing status.
- Handler writes go through a child DI scope — the handler is safe to use from any pipeline, including background hosted services that have no ambient request scope.

## 17. Agent Runtimes and Connectors Are Plugins

Every agent runtime (`IAgentRuntime`) and connector (`IConnectorType`) is a first-class extension point. The host references the abstraction only; concrete implementations live in their own `Cvoya.Spring.AgentRuntimes.<Name>` / `Cvoya.Spring.Connector.<Name>` project and register via DI.

### Project layout

- **Agent runtimes** live under `src/Cvoya.Spring.AgentRuntimes.<Name>/` and reference `Cvoya.Spring.Core` only (no Dapr, no ASP.NET runtime). Each project ships:
  - A single `AddCvoyaSpringAgentRuntime<Name>()` DI extension (registered with `TryAddEnumerable(ServiceDescriptor.Singleton<IAgentRuntime, …>)` so a cloud overlay can pre-register a variant without displacing the OSS default).
  - A `seed.json` at `agent-runtimes/<id>/seed.json` carrying the runtime's `DefaultModels` catalog.
  - A per-project `README.md` documenting the runtime's id, tool kind, credential schema, and any host-side baseline tooling the runtime expects.
- **Connectors** live under `src/Cvoya.Spring.Connector.<Name>/` and reference `Cvoya.Spring.Connectors.Abstractions` (which itself references `Cvoya.Spring.Core` plus the ASP.NET shared framework so connectors can own their typed HTTP routes). Each connector exposes `AddCvoyaSpringConnector<Name>(IConfiguration configuration)` and registers its `IConnectorType` as a singleton. Connector-specific HTTP routes attach via the `MapRoutes(IEndpointRouteBuilder group)` contract — the host calls it on a pre-scoped `/api/v1/connectors/{slug}` group so the connector package stays ignorant of the outer path shape.

### Tenant install surfaces

Both plugin kinds sit behind a **tenant install table** (`tenant_agent_runtime_installs`, `tenant_connector_installs`) managed by `ITenantAgentRuntimeInstallService` / `ITenantConnectorInstallService`. A plugin registered in DI is _available_ to the host; an install row makes it _visible_ to a given tenant. Bootstrap seeds default-tenant installs for every registered plugin; subsequent lifecycle goes through the install service.

### Credential-health wiring

Plugins that authenticate via `HttpClient` MUST wire `AddCredentialHealthWatchdog(kind, subjectId, secretName)` onto their named client (see `CONVENTIONS.md` § 16). Without it, revoked or expired tokens surface only on unit failure, not on accumulating signal. For **agent runtimes**, credential probing was moved into the container as of #941 — runtimes expose `GetProbeSteps(config, credential)` and the Dapr `UnitValidationWorkflow` runs every probe inside the unit's chosen image (see `docs/architecture/units.md#unit-validation-workflow`). For **connectors**, the accept-time path remains `POST /validate-credential` → `IConnectorType.ValidateCredentialAsync`.

### Adding a new plugin

1. Create `src/Cvoya.Spring.<Kind>.<Name>/` with a single DI-extension entry point. Reference `Cvoya.Spring.Core` (runtimes) or `Cvoya.Spring.Connectors.Abstractions` (connectors) only.
2. Implement the contract; wire the credential-health watchdog on any HttpClient that authenticates.
3. For runtimes, ship a `seed.json` and append a row to the "Built-in agent runtimes" table in `AGENTS.md`. For connectors, document the typed routes exposed via `MapRoutes` in the project `README.md`.
4. Register the DI extension from `src/Cvoya.Spring.Host.Api/Program.cs`. No changes to `Cvoya.Spring.Dapr` are required — the install surface, registry, and bootstrap pick up the new plugin automatically.
