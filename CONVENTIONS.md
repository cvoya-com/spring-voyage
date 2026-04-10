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

## 1. Project Structure

```
├── src/
│   ├── Cvoya.Spring.Core/              # Domain: interfaces, types — NO external dependencies
│   │   ├── Messaging/                  # IAddressable, IMessageReceiver, Message, Address
│   │   ├── Orchestration/              # IOrchestrationStrategy, IUnitContext
│   │   ├── Capabilities/              # IExpertiseProvider, IActivityObservable, ICapabilityProvider
│   │   ├── Execution/                 # ExecutionMode, IExecutionDispatcher, IAiProvider
│   │   ├── Skills/                    # Skill, ToolDefinition records
│   │   └── Directory/                 # IDirectoryService, DirectoryEntry
│   ├── Cvoya.Spring.Dapr/             # Dapr implementations of Core interfaces
│   │   ├── Actors/                    # AgentActor, UnitActor, ConnectorActor, HumanActor
│   │   ├── Orchestration/            # AI, Workflow, Hybrid, RuleBased, Peer strategies
│   │   ├── Execution/                # HostedExecutionDispatcher, DelegatedExecutionDispatcher
│   │   ├── Routing/                  # MessageRouter, DirectoryService, DirectoryCache
│   │   ├── Prompts/                  # PromptAssembler (4-layer)
│   │   ├── Workflows/               # Platform-internal Dapr Workflows
│   │   └── DependencyInjection/      # ServiceCollectionExtensions
│   ├── Cvoya.Spring.A2A/             # A2A protocol (stub in Phase 1)
│   ├── Cvoya.Spring.Connector.GitHub/ # GitHub connector
│   ├── Cvoya.Spring.Host.Api/        # ASP.NET Core Web API host
│   ├── Cvoya.Spring.Host.Worker/     # Headless worker host (actor runtime)
│   ├── Cvoya.Spring.Cli/            # CLI ("spring" command)
│   └── Cvoya.Spring.Web/            # Web UI (stub in Phase 1)
├── tests/
│   ├── Cvoya.Spring.Core.Tests/
│   ├── Cvoya.Spring.Dapr.Tests/
│   ├── Cvoya.Spring.Connector.GitHub.Tests/
│   ├── Cvoya.Spring.Host.Api.Tests/
│   ├── Cvoya.Spring.Cli.Tests/
│   └── Cvoya.Spring.Integration.Tests/
├── packages/
│   └── software-engineering/          # Domain package
├── dapr/
│   ├── components/                   # Dapr component YAML
│   └── configuration/               # Access control, resiliency
└── SpringVoyage.slnx
```

**Rules:**
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
