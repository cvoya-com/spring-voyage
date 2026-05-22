// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Api.Tests.Services;

using Cvoya.Spring.Core.Artefacts;
using Cvoya.Spring.Core.Catalog;
using Cvoya.Spring.Core.Execution;
using Cvoya.Spring.Core.Identifiers;
using Cvoya.Spring.Core.Lifecycle;
using Cvoya.Spring.Core.ModelProviders;
using Cvoya.Spring.Dapr.Actors;
using Cvoya.Spring.Host.Api.Services;

using global::Dapr.Actors;
using global::Dapr.Actors.Client;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using NSubstitute;

using Shouldly;

using Xunit;

/// <summary>
/// Unit tests for <see cref="ArtefactAutoStartGate"/> — the shared
/// auto-start gate consumed by <see cref="UnitCreationService"/>,
/// <see cref="DefaultPackageArtefactActivator"/>, and
/// <c>AgentEndpoints.CreateAgentAsync</c> (#2374). The gate is the single
/// place where the per-kind execution-store / credential-resolver
/// branching lives, so this is the focused place to pin the precondition
/// logic and the Validating + SetPendingAutoStart side effects.
/// </summary>
/// <remarks>
/// The gate creates a fresh DI scope per call to resolve the per-kind
/// stores; tests substitute the <see cref="IServiceScopeFactory"/> so the
/// scope's <see cref="IServiceProvider"/> can hand back configured mocks.
/// </remarks>
public class ArtefactAutoStartGateTests
{
    private static readonly Guid UnitGuid = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid AgentGuid = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private readonly IServiceScopeFactory _scopeFactory = Substitute.For<IServiceScopeFactory>();
    private readonly IServiceProvider _scopeProvider = Substitute.For<IServiceProvider>();
    private readonly IActorProxyFactory _actorProxyFactory = Substitute.For<IActorProxyFactory>();
    private readonly ILoggerFactory _loggerFactory = Substitute.For<ILoggerFactory>();
    private readonly IUnitExecutionStore _unitStore = Substitute.For<IUnitExecutionStore>();
    private readonly IAgentExecutionStore _agentStore = Substitute.For<IAgentExecutionStore>();
    private readonly IRuntimeCatalog _runtimeCatalog = Substitute.For<IRuntimeCatalog>();
    private readonly ILlmCredentialResolver _credentialResolver = Substitute.For<ILlmCredentialResolver>();

    private readonly ArtefactAutoStartGate _gate;

    public ArtefactAutoStartGateTests()
    {
        _loggerFactory.CreateLogger(Arg.Any<string>()).Returns(Substitute.For<ILogger>());

        // Wire the scope factory to return a scope whose ServiceProvider
        // resolves the per-kind store, runtime catalogue, and credential
        // resolver from configured fields.
        var scope = Substitute.For<IServiceScope, IAsyncDisposable>();
        scope.ServiceProvider.Returns(_scopeProvider);
        _scopeFactory.CreateScope().Returns(scope);
        _scopeProvider.GetService(typeof(IUnitExecutionStore)).Returns(_unitStore);
        _scopeProvider.GetService(typeof(IAgentExecutionStore)).Returns(_agentStore);
        _scopeProvider.GetService(typeof(IRuntimeCatalog)).Returns(_runtimeCatalog);
        _scopeProvider.GetService(typeof(ILlmCredentialResolver)).Returns(_credentialResolver);

        _gate = new ArtefactAutoStartGate(_scopeFactory, _actorProxyFactory, _loggerFactory);
    }

    private void ArrangeRuntimeWithOauthEdge(string runtimeId, string providerId)
    {
        _runtimeCatalog.GetAgentRuntime(runtimeId).Returns(new AgentRuntime(
            Id: runtimeId,
            DisplayName: runtimeId,
            DefaultImage: "ghcr.io/test:latest",
            Launcher: "test-launcher",
            ThreadBinding: new ThreadBinding(ThreadBindingKind.None, ArgName: null, EnvVarName: null),
            SystemPromptInjection: new SystemPromptInjection(
                SystemPromptInjectionKind.EnvVar,
                FilePath: null,
                EnvVarName: "SP",
                ArgName: null),
            ModelProviders:
            [
                new AgentRuntimeProviderEdge(
                    Id: providerId,
                    AuthMethod: AuthMethod.Oauth,
                    CredentialEnvVar: "X"),
            ]));
    }

    private void ArrangeRuntimeWithNoAuth(string runtimeId, string providerId)
    {
        _runtimeCatalog.GetAgentRuntime(runtimeId).Returns(new AgentRuntime(
            Id: runtimeId,
            DisplayName: runtimeId,
            DefaultImage: "ghcr.io/test:latest",
            Launcher: "test-launcher",
            ThreadBinding: new ThreadBinding(ThreadBindingKind.None, ArgName: null, EnvVarName: null),
            SystemPromptInjection: new SystemPromptInjection(
                SystemPromptInjectionKind.EnvVar,
                FilePath: null,
                EnvVarName: "SP",
                ArgName: null),
            ModelProviders:
            [
                new AgentRuntimeProviderEdge(
                    Id: providerId,
                    AuthMethod: null,
                    CredentialEnvVar: null),
            ]));
    }

    private void ArrangeCredential(string providerId, string? value = "secret-value")
    {
        _credentialResolver
            .ResolveAsync(providerId, Arg.Any<AuthMethod>(), Arg.Any<Guid?>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(new LlmCredentialResolution(value, LlmCredentialSource.Tenant, "test-secret"));
    }

    // ──────────────────────────────────────────────────────────────────────
    // Skip kinds — no container lifecycle (ADR-0046 §2 dropped Workflow;
    // HumanTemplate is package-resolution-only).
    // ──────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(ArtefactKind.Skill)]
    [InlineData(ArtefactKind.HumanTemplate)]
    public async Task TryAutoStartAsync_NonLifecycleKind_ReturnsDraft(ArtefactKind kind)
    {
        var result = await _gate.TryAutoStartAsync(kind, Guid.NewGuid(), "x", TestContext.Current.CancellationToken);

        result.ShouldBe(LifecycleStatus.Draft);
        // No execution-store read, no actor proxy creation.
        await _unitStore.DidNotReceiveWithAnyArgs().GetAsync(default!, TestContext.Current.CancellationToken);
        await _agentStore.DidNotReceiveWithAnyArgs().GetAsync(default!, TestContext.Current.CancellationToken);
    }

    // ──────────────────────────────────────────────────────────────────────
    // Per-kind store routing
    // ──────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task TryAutoStartAsync_Unit_ReadsUnitExecutionStore()
    {
        var actorId = GuidFormatter.Format(UnitGuid);
        _unitStore.GetAsync(actorId, Arg.Any<CancellationToken>())
            .Returns(new UnitExecutionDefaults(
                Image: "ghcr.io/test:1",
                Model: new Model("anthropic", "claude-sonnet-4"),
                Runtime: "claude-code"));
        ArrangeRuntimeWithOauthEdge("claude-code", "anthropic");
        ArrangeCredential("anthropic");
        _actorProxyFactory.CreateActorProxy<IUnitActor>(Arg.Any<ActorId>(), nameof(UnitActor))
            .Returns(ci =>
            {
                var proxy = Substitute.For<IUnitActor>();
                proxy.TransitionAsync(LifecycleStatus.Validating, Arg.Any<CancellationToken>())
                    .Returns(new TransitionResult(true, LifecycleStatus.Validating, null));
                return proxy;
            });

        var result = await _gate.TryAutoStartAsync(
            ArtefactKind.Unit, UnitGuid, "test-unit", TestContext.Current.CancellationToken);

        result.ShouldBe(LifecycleStatus.Validating);
        await _unitStore.Received(1).GetAsync(actorId, Arg.Any<CancellationToken>());
        await _agentStore.DidNotReceiveWithAnyArgs().GetAsync(default!, TestContext.Current.CancellationToken);
        // Credential resolver for Unit kind must pass unitId, not agentId.
        await _credentialResolver.Received(1).ResolveAsync(
            "anthropic", Arg.Any<AuthMethod>(),
            agentId: null, unitId: UnitGuid, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TryAutoStartAsync_Agent_ReadsAgentExecutionStore()
    {
        var actorId = GuidFormatter.Format(AgentGuid);
        _agentStore.GetAsync(actorId, Arg.Any<CancellationToken>())
            .Returns(new AgentExecutionShape(
                Image: "ghcr.io/test:1",
                Model: new Model("anthropic", "claude-sonnet-4"),
                Hosting: null,
                Runtime: "claude-code"));
        ArrangeRuntimeWithOauthEdge("claude-code", "anthropic");
        ArrangeCredential("anthropic");
        _actorProxyFactory.CreateActorProxy<IAgentActor>(Arg.Any<ActorId>(), nameof(AgentActor))
            .Returns(ci =>
            {
                var proxy = Substitute.For<IAgentActor>();
                proxy.TransitionAsync(LifecycleStatus.Validating, Arg.Any<CancellationToken>())
                    .Returns(new TransitionResult(true, LifecycleStatus.Validating, null));
                return proxy;
            });

        var result = await _gate.TryAutoStartAsync(
            ArtefactKind.Agent, AgentGuid, "test-agent", TestContext.Current.CancellationToken);

        result.ShouldBe(LifecycleStatus.Validating);
        await _agentStore.Received(1).GetAsync(actorId, Arg.Any<CancellationToken>());
        await _unitStore.DidNotReceiveWithAnyArgs().GetAsync(default!, TestContext.Current.CancellationToken);
        // Credential resolver for Agent kind must pass agentId, not unitId.
        await _credentialResolver.Received(1).ResolveAsync(
            "anthropic", Arg.Any<AuthMethod>(),
            agentId: AgentGuid, unitId: null, Arg.Any<CancellationToken>());
    }

    // ──────────────────────────────────────────────────────────────────────
    // Preconditions — missing config keeps the artefact in Draft
    // ──────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task TryAutoStartAsync_Agent_NoExecutionDefaults_ReturnsDraft()
    {
        _agentStore.GetAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((AgentExecutionShape?)null);

        var result = await _gate.TryAutoStartAsync(
            ArtefactKind.Agent, AgentGuid, "test-agent", TestContext.Current.CancellationToken);

        result.ShouldBe(LifecycleStatus.Draft);
        _actorProxyFactory.DidNotReceiveWithAnyArgs().CreateActorProxy<IAgentActor>(default!, default!);
    }

    [Fact]
    public async Task TryAutoStartAsync_Agent_MissingImage_ReturnsDraft()
    {
        _agentStore.GetAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new AgentExecutionShape(
                Image: null, Model: new Model("anthropic", "claude-sonnet-4"),
                Hosting: null, Runtime: "claude-code"));

        var result = await _gate.TryAutoStartAsync(
            ArtefactKind.Agent, AgentGuid, "x", TestContext.Current.CancellationToken);

        result.ShouldBe(LifecycleStatus.Draft);
    }

    [Fact]
    public async Task TryAutoStartAsync_Agent_MissingModel_ReturnsDraft()
    {
        _agentStore.GetAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new AgentExecutionShape(
                Image: "ghcr.io/test:1", Model: null,
                Hosting: null, Runtime: "claude-code"));

        var result = await _gate.TryAutoStartAsync(
            ArtefactKind.Agent, AgentGuid, "x", TestContext.Current.CancellationToken);

        result.ShouldBe(LifecycleStatus.Draft);
    }

    [Fact]
    public async Task TryAutoStartAsync_Agent_RuntimeNotInCatalogue_ReturnsDraft()
    {
        _agentStore.GetAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new AgentExecutionShape(
                Image: "ghcr.io/test:1", Model: new Model("anthropic", "claude-sonnet-4"),
                Hosting: null, Runtime: "claude-code"));
        _runtimeCatalog.GetAgentRuntime("claude-code").Returns((AgentRuntime?)null);

        var result = await _gate.TryAutoStartAsync(
            ArtefactKind.Agent, AgentGuid, "x", TestContext.Current.CancellationToken);

        result.ShouldBe(LifecycleStatus.Draft);
    }

    [Fact]
    public async Task TryAutoStartAsync_Agent_CredentialMissing_ReturnsDraft()
    {
        _agentStore.GetAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new AgentExecutionShape(
                Image: "ghcr.io/test:1", Model: new Model("anthropic", "claude-sonnet-4"),
                Hosting: null, Runtime: "claude-code"));
        ArrangeRuntimeWithOauthEdge("claude-code", "anthropic");
        ArrangeCredential("anthropic", value: null);

        var result = await _gate.TryAutoStartAsync(
            ArtefactKind.Agent, AgentGuid, "x", TestContext.Current.CancellationToken);

        result.ShouldBe(LifecycleStatus.Draft);
        _actorProxyFactory.DidNotReceiveWithAnyArgs().CreateActorProxy<IAgentActor>(default!, default!);
    }

    [Fact]
    public async Task TryAutoStartAsync_Agent_RuntimeWithoutCredential_SkipsCredentialCheck()
    {
        // Ollama-style edge: no AuthMethod declared → the gate must NOT
        // require a credential; image + model + runtime is sufficient.
        _agentStore.GetAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new AgentExecutionShape(
                Image: "ghcr.io/test:1", Model: new Model("ollama", "qwen3"),
                Hosting: null, Runtime: "spring-voyage"));
        ArrangeRuntimeWithNoAuth("spring-voyage", "ollama");
        _actorProxyFactory.CreateActorProxy<IAgentActor>(Arg.Any<ActorId>(), nameof(AgentActor))
            .Returns(ci =>
            {
                var proxy = Substitute.For<IAgentActor>();
                proxy.TransitionAsync(LifecycleStatus.Validating, Arg.Any<CancellationToken>())
                    .Returns(new TransitionResult(true, LifecycleStatus.Validating, null));
                return proxy;
            });

        var result = await _gate.TryAutoStartAsync(
            ArtefactKind.Agent, AgentGuid, "x", TestContext.Current.CancellationToken);

        result.ShouldBe(LifecycleStatus.Validating);
        // No-auth edges must NOT consult the credential resolver.
        await _credentialResolver.DidNotReceiveWithAnyArgs().ResolveAsync(
            default!, default, default, default, TestContext.Current.CancellationToken);
    }

    // ──────────────────────────────────────────────────────────────────────
    // Side effects when the gate passes
    // ──────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task TryAutoStartAsync_Agent_GateOpens_ArmsPendingAutoStart()
    {
        var proxy = Substitute.For<IAgentActor>();
        proxy.TransitionAsync(LifecycleStatus.Validating, Arg.Any<CancellationToken>())
            .Returns(new TransitionResult(true, LifecycleStatus.Validating, null));
        _actorProxyFactory.CreateActorProxy<IAgentActor>(Arg.Any<ActorId>(), nameof(AgentActor))
            .Returns(proxy);
        _agentStore.GetAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new AgentExecutionShape(
                Image: "ghcr.io/test:1", Model: new Model("anthropic", "claude-sonnet-4"),
                Hosting: null, Runtime: "claude-code"));
        ArrangeRuntimeWithOauthEdge("claude-code", "anthropic");
        ArrangeCredential("anthropic");

        var result = await _gate.TryAutoStartAsync(
            ArtefactKind.Agent, AgentGuid, "test-agent", TestContext.Current.CancellationToken);

        result.ShouldBe(LifecycleStatus.Validating);
        await proxy.Received(1).TransitionAsync(LifecycleStatus.Validating, Arg.Any<CancellationToken>());
        await proxy.Received(1).SetPendingAutoStartAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TryAutoStartAsync_Agent_TransitionRejected_DoesNotSetPendingAutoStart()
    {
        var proxy = Substitute.For<IAgentActor>();
        proxy.TransitionAsync(LifecycleStatus.Validating, Arg.Any<CancellationToken>())
            .Returns(new TransitionResult(false, LifecycleStatus.Running, "already running"));
        _actorProxyFactory.CreateActorProxy<IAgentActor>(Arg.Any<ActorId>(), nameof(AgentActor))
            .Returns(proxy);
        _agentStore.GetAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new AgentExecutionShape(
                Image: "ghcr.io/test:1", Model: new Model("anthropic", "claude-sonnet-4"),
                Hosting: null, Runtime: "claude-code"));
        ArrangeRuntimeWithOauthEdge("claude-code", "anthropic");
        ArrangeCredential("anthropic");

        var result = await _gate.TryAutoStartAsync(
            ArtefactKind.Agent, AgentGuid, "x", TestContext.Current.CancellationToken);

        result.ShouldBe(LifecycleStatus.Draft);
        await proxy.DidNotReceive().SetPendingAutoStartAsync(Arg.Any<CancellationToken>());
    }
}
