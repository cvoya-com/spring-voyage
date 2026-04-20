# Spring Voyage V2 — Coding Conventions

These conventions ensure that code from parallel agents merges cleanly. All agents (Claude Code, Cursor, GitHub Copilot) must follow these rules.

## Architecture Reference

- **Architecture plan:** `docs/SpringVoyage-v2-plan.md` — the source of truth for all design decisions
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

This repo is the OSS core of a two-repo model. A private repository extends it via DI and git submodule. All code must be designed for clean extension. See `AGENTS.md` § "Open-Source Platform & Extensibility" for the full rationale.

**Service registration:** Always use `TryAdd*` (`TryAddSingleton`, `TryAddScoped`, `TryAddTransient`). This lets the private repo pre-register overrides.

**Sealing policy:** Don't `seal` services, handlers, base classes, or strategy implementations. Seal only leaf types that are not extension points (e.g., record DTOs, internal helpers).

**Visibility:** Types that are part of the extension contract must be `public`. Use `internal` only for true implementation details that no consumer would ever need to access or replace.

**Base class hooks:** When creating base classes (`ConnectorBase`, `ActorBase`, etc.), make template/hook methods `protected virtual` so they can be overridden.

**No tenant assumptions:** Don't embed single-user or single-deployment assumptions. Use injected services for anything the private repo might scope per-tenant: repositories, configuration providers, policy evaluators.

**No statics for state or services:** Everything goes through DI. No static service locators, no ambient contexts, no `static` mutable state.

**Cross-tenant reads and writes go through `ITenantScopeBypass`.** The EF Core query filter applied to every tenant-scoped entity restricts reads and writes to the current tenant. A small set of operations legitimately need to cross that boundary — `DatabaseMigrator`, platform-wide analytics, system administration. Those call sites wrap the work in `ITenantScopeBypass.BeginBypass(reason)` so the bypass is auditable (structured log on open and close, with caller context and duration) and so the private cloud repo can swap the default implementation for a permission-checked variant. Never call `IgnoreQueryFilters()` directly in business code — if a feature seems to need it, rethink the feature or file an issue.

## 14. UI / CLI Feature Parity

Every user-facing feature must ship through BOTH the web portal UI and the `spring` CLI. Neither surface is allowed to drift ahead of the other.

- When planning a feature PR, enumerate the affected surfaces (API endpoints, UI screens, CLI commands). If a surface is missing, either include it in the same PR or file a sibling issue before the PR lands so the gap is tracked.
- Treat "the UI can do X but the CLI can't" (or vice versa) as a real bug, not a speculative nice-to-have.
- Link CLI-side and UI-side issues as siblings when a feature is split across PRs.
- An E2E scenario under `tests/e2e/` is a good parity proxy: if the scenario has to fall back to `curl` because the CLI lacks the command, the CLI is behind.

**Exceptions:** admin/ops operations that are genuinely dev-only (e.g., `dotnet ef migrations add`) don't need a UI counterpart. Internal test affordances are also out of scope.
