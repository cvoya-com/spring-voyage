// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Tests.Execution;

using Cvoya.Spring.Core;
using Cvoya.Spring.Core.Catalog;
using Cvoya.Spring.Core.Execution;
using Cvoya.Spring.Core.Identifiers;
using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Core.ModelProviders;
using Cvoya.Spring.Core.Orchestration;
using Cvoya.Spring.Dapr.Data;
using Cvoya.Spring.Dapr.Execution;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using NSubstitute;
using NSubstitute.ExceptionExtensions;

using Shouldly;

using Xunit;

/// <summary>
/// Focused unit tests for <see cref="PersistentAgentLifecycle"/>. Verifies
/// the validation branches (no definition, wrong hosting mode, missing image)
/// and the idempotent undeploy path. The happy-path deploy (container start +
/// readiness probe + registry registration) is covered by the existing
/// PersistentDispatchIntegrationTests; the readiness probe requires an HTTP
/// fake that is awkward to set up here, so we exercise the validation paths
/// that don't need it.
/// </summary>
public class PersistentAgentLifecycleTests
{
    private static readonly Guid AgentAGuid = new("aaaaaaaa-bbbb-1111-1111-000000000001");
    private static readonly Guid AgentEGuid = new("aaaaaaaa-bbbb-1111-1111-000000000002");
    private static readonly Guid GhostGuid = new("aaaaaaaa-bbbb-1111-1111-000000000099");
    private static readonly string AgentAId = GuidFormatter.Format(AgentAGuid);
    private static readonly string AgentEId = GuidFormatter.Format(AgentEGuid);
    private static readonly string GhostId = GuidFormatter.Format(GhostGuid);

    private readonly IContainerRuntime _containerRuntime = Substitute.For<IContainerRuntime>();
    private readonly IAgentDefinitionProvider _agentProvider = Substitute.For<IAgentDefinitionProvider>();
    private readonly IMcpServer _mcpServer = Substitute.For<IMcpServer>();
    private readonly IAgentRuntimeLauncher _launcher = Substitute.For<IAgentRuntimeLauncher>();
    private readonly ILoggerFactory _loggerFactory = Substitute.For<ILoggerFactory>();
    private readonly IHttpClientFactory _httpClientFactory = Substitute.For<IHttpClientFactory>();
    private readonly PersistentAgentRegistry _registry;
    private readonly PersistentAgentLifecycle _lifecycle;

    public PersistentAgentLifecycleTests()
    {
        _loggerFactory.CreateLogger(Arg.Any<string>()).Returns(Substitute.For<ILogger>());
        // ADR-0038: launcher.Kind matches the catalogue runtime entry's
        // launcher strategy id (claude-code-cli for the claude runtime).
        _launcher.Kind.Returns("claude-code-cli");

        // ADR-0038: catalogue maps the runtime id ("claude") to the
        // launcher strategy id so the lifecycle can derive the launcher.
        var claudeRuntime = new Cvoya.Spring.Core.Catalog.AgentRuntime(
            Id: "claude",
            DisplayName: "Claude",
            DefaultImage: "ghcr.io/test/claude:latest",
            Launcher: "claude-code-cli",
            ThreadBinding: new ThreadBinding(ThreadBindingKind.CliArg, ArgName: "--resume"),
            SystemPromptInjection: new SystemPromptInjection(SystemPromptInjectionKind.File, FilePath: "AGENTS.md"),
            ModelProviders: new[]
            {
                new AgentRuntimeProviderEdge(
                    Id: "anthropic",
                    AuthMethod: AuthMethod.Oauth,
                    CredentialEnvVar: "CLAUDE_CODE_OAUTH_TOKEN"),
            });
        var catalog = Substitute.For<IRuntimeCatalog>();
        catalog.GetAgentRuntime("claude").Returns(claudeRuntime);

        // ADR-0039 D3: lifecycle now resolves orchestration tools per-deploy.
        // Default to "no orchestration tools" so the validation-path tests
        // here remain agnostic to the orchestration surface.
        var messagingToolProvider = Substitute.For<IMessagingToolProvider>();
        messagingToolProvider.GetMessagingTools(Arg.Any<Address>(), Arg.Any<Guid>())
            .Returns(Array.Empty<MessagingToolDescriptor>());

        var daprOptions = new DaprSidecarOptions();
        var services = new ServiceCollection();
        services.AddSingleton(_containerRuntime);
        services.AddSingleton(_httpClientFactory);
        services.AddSingleton(_loggerFactory);
        services.AddSingleton(Substitute.For<IDaprSidecarManager>());
        services.AddSingleton(Options.Create(daprOptions));
        services.AddSingleton<ContainerLifecycleManager>();
        services.AddSingleton<AgentVolumeManager>();
        services.AddSingleton(_agentProvider);
        services.AddSingleton(_mcpServer);
        services.AddSingleton(_launcher);
        services.AddSingleton<IEnumerable<IAgentRuntimeLauncher>>(_ => new[] { _launcher });
        services.AddSingleton(catalog);
        services.AddSingleton(messagingToolProvider);
        var agentContextBuilder = Substitute.For<IAgentContextBuilder>();
        agentContextBuilder
            .BuildAsync(Arg.Any<AgentLaunchContext>(), Arg.Any<CancellationToken>())
            .Returns(new AgentBootstrapContext(
                new Dictionary<string, string>(),
                new Dictionary<string, string>()));
        services.AddSingleton(agentContextBuilder);
        services.AddSingleton<PersistentAgentRegistry>();
        services.AddSingleton<PersistentAgentLifecycle>();
        // #2468: registry now persists state via EF. Tests share an
        // in-memory DB per fixture so the Deploy / Undeploy round-trips
        // observe each other.
        var dbName = $"PersistentAgentLifecycleTests-{Guid.NewGuid()}";
        services.AddDbContext<SpringDbContext>(options =>
            options.UseInMemoryDatabase(dbName));
        var sp = services.BuildServiceProvider();
        _registry = sp.GetRequiredService<PersistentAgentRegistry>();
        _lifecycle = sp.GetRequiredService<PersistentAgentLifecycle>();
    }

    [Fact]
    public async Task Deploy_WhenAgentMissing_Throws()
    {
        var ct = TestContext.Current.CancellationToken;
        _agentProvider.GetByIdAsync(GhostId, Arg.Any<CancellationToken>())
            .Returns((AgentDefinition?)null);

        var ex = await Should.ThrowAsync<SpringException>(
            () => _lifecycle.DeployAsync(GhostId, cancellationToken: ct));

        ex.Message.ShouldContain(GhostId);
    }

    [Fact]
    public async Task Deploy_WhenExecutionMissing_Throws()
    {
        var ct = TestContext.Current.CancellationToken;
        _agentProvider.GetByIdAsync(AgentAId, Arg.Any<CancellationToken>())
            .Returns(new AgentDefinition(AgentAId, "A", null, Execution: null));

        var ex = await Should.ThrowAsync<SpringException>(
            () => _lifecycle.DeployAsync(AgentAId, cancellationToken: ct));

        ex.Message.ShouldContain("execution");
    }

    [Fact]
    public async Task Deploy_WhenAgentIsEphemeral_Throws()
    {
        var ct = TestContext.Current.CancellationToken;
        _agentProvider.GetByIdAsync(AgentEId, Arg.Any<CancellationToken>())
            .Returns(new AgentDefinition(
                AgentEId,
                "E",
                null,
                new AgentExecutionConfig("claude", "img", Hosting: AgentHostingMode.Ephemeral)));

        var ex = await Should.ThrowAsync<SpringException>(
            () => _lifecycle.DeployAsync(AgentEId, cancellationToken: ct));

        ex.Message.ShouldContain("persistent");
    }

    [Fact]
    public async Task Deploy_WhenDefinitionMissingImageAndNoOverride_Throws()
    {
        var ct = TestContext.Current.CancellationToken;
        _agentProvider.GetByIdAsync(AgentAId, Arg.Any<CancellationToken>())
            .Returns(new AgentDefinition(
                AgentAId,
                "A",
                null,
                new AgentExecutionConfig("claude", Image: null, Hosting: AgentHostingMode.Persistent)));

        var ex = await Should.ThrowAsync<SpringException>(
            () => _lifecycle.DeployAsync(AgentAId, cancellationToken: ct));

        ex.Message.ShouldContain("image");
    }

    [Fact]
    public async Task Deploy_WhenAlreadyHealthy_ReturnsExistingWithoutStartingContainer()
    {
        var ct = TestContext.Current.CancellationToken;
        var endpoint = new Uri("http://localhost:8999/");
        await _registry.RegisterAsync(
            AgentAId,
            endpoint,
            "container-abc",
            new AgentDefinition(
                AgentAId,
                "A",
                null,
                new AgentExecutionConfig("claude", "img", Hosting: AgentHostingMode.Persistent)),
            cancellationToken: ct);

        _agentProvider.GetByIdAsync(AgentAId, Arg.Any<CancellationToken>())
            .Returns(new AgentDefinition(
                AgentAId,
                "A",
                null,
                new AgentExecutionConfig("claude", "img", Hosting: AgentHostingMode.Persistent)));

        var result = await _lifecycle.DeployAsync(AgentAId, cancellationToken: ct);

        result.ContainerId.ShouldBe("container-abc");
        // No StartAsync call because the idempotent fast-path returned the
        // pre-registered entry.
        await _containerRuntime.DidNotReceive()
            .StartAsync(Arg.Any<ContainerConfig>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Undeploy_WhenAgentNotRegistered_ReturnsFalse()
    {
        var ct = TestContext.Current.CancellationToken;
        var result = await _lifecycle.UndeployAsync(GhostId, ct);
        result.ShouldBeFalse();
    }

    [Fact]
    public async Task Undeploy_WhenAgentRegistered_StopsContainerAndReturnsTrue()
    {
        var ct = TestContext.Current.CancellationToken;
        var endpoint = new Uri("http://localhost:8999/");
        await _registry.RegisterAsync(AgentAId, endpoint, "container-abc", definition: null, cancellationToken: ct);

        var result = await _lifecycle.UndeployAsync(AgentAId, ct);

        result.ShouldBeTrue();
        await _containerRuntime.Received()
            .StopAsync("container-abc", Arg.Any<CancellationToken>());
        var entry = await _registry.TryGetAsync(AgentAId, ct);
        entry.ShouldBeNull();
    }

    [Fact]
    public async Task Scale_WithZeroReplicas_Undeploys()
    {
        var ct = TestContext.Current.CancellationToken;
        var endpoint = new Uri("http://localhost:8999/");
        await _registry.RegisterAsync(AgentAId, endpoint, "container-abc", definition: null, cancellationToken: ct);

        await _lifecycle.ScaleAsync(AgentAId, 0, ct);

        var entry = await _registry.TryGetAsync(AgentAId, ct);
        entry.ShouldBeNull();
    }

    [Fact]
    public async Task Scale_WithMoreThanOneReplica_Throws()
    {
        var ct = TestContext.Current.CancellationToken;
        var ex = await Should.ThrowAsync<SpringException>(
            () => _lifecycle.ScaleAsync(AgentAId, 2, ct));

        ex.Message.ShouldContain("Horizontal scaling");
    }

    [Fact]
    public async Task GetLogs_WhenAgentNotDeployed_Throws()
    {
        var ct = TestContext.Current.CancellationToken;
        var ex = await Should.ThrowAsync<SpringException>(
            () => _lifecycle.GetLogsAsync(GhostId, cancellationToken: ct));

        ex.Message.ShouldContain("not deployed");
    }

    [Fact]
    public async Task Deploy_LeafAgentDefinition_IssuesAgentSchemeSession()
    {
        var ct = TestContext.Current.CancellationToken;
        var parentUnitId = GuidFormatter.Format(Guid.NewGuid());
        _agentProvider.GetByIdAsync(AgentAId, Arg.Any<CancellationToken>())
            .Returns(new AgentDefinition(
                AgentAId,
                "A",
                null,
                new AgentExecutionConfig("claude", "img", Hosting: AgentHostingMode.Persistent),
                UnitId: parentUnitId));

        _mcpServer.Endpoint.Returns("http://localhost:5040/mcp");
        _mcpServer.IssueSession(AgentAId, $"persistent-{AgentAId}", Address.AgentScheme)
            .Returns(new McpSession("tok", AgentAId, $"persistent-{AgentAId}", Address.AgentScheme,
                new Address(Address.AgentScheme, AgentAGuid)));
        _launcher.PrepareAsync(Arg.Any<AgentLaunchContext>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("test stops here"));

        await Should.ThrowAsync<InvalidOperationException>(
            () => _lifecycle.DeployAsync(AgentAId, cancellationToken: ct));

        _mcpServer.Received(1).IssueSession(AgentAId, $"persistent-{AgentAId}", Address.AgentScheme);
    }

    [Fact]
    public async Task Deploy_UnitAsAgentDefinition_IssuesUnitSchemeSession()
    {
        var ct = TestContext.Current.CancellationToken;
        _agentProvider.GetByIdAsync(AgentAId, Arg.Any<CancellationToken>())
            .Returns(new AgentDefinition(
                AgentAId,
                "A",
                null,
                new AgentExecutionConfig("claude", "img", Hosting: AgentHostingMode.Persistent),
                UnitId: AgentAId));

        _mcpServer.Endpoint.Returns("http://localhost:5040/mcp");
        _mcpServer.IssueSession(AgentAId, $"persistent-{AgentAId}", Address.UnitScheme)
            .Returns(new McpSession("tok", AgentAId, $"persistent-{AgentAId}", Address.UnitScheme,
                new Address(Address.UnitScheme, AgentAGuid)));
        AgentLaunchContext? captured = null;
        _launcher.PrepareAsync(Arg.Do<AgentLaunchContext>(c => captured = c), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("test stops here"));

        await Should.ThrowAsync<InvalidOperationException>(
            () => _lifecycle.DeployAsync(AgentAId, cancellationToken: ct));

        _mcpServer.Received(1).IssueSession(AgentAId, $"persistent-{AgentAId}", Address.UnitScheme);
        captured.ShouldNotBeNull();
        captured!.AgentAddress!.Scheme.ShouldBe(Address.UnitScheme);
    }

    [Fact]
    public async Task GetLogs_ForwardsToContainerRuntime()
    {
        var ct = TestContext.Current.CancellationToken;
        var endpoint = new Uri("http://localhost:8999/");
        await _registry.RegisterAsync(AgentAId, endpoint, "container-abc", definition: null, cancellationToken: ct);

        _containerRuntime
            .GetLogsAsync("container-abc", 50, Arg.Any<CancellationToken>())
            .Returns("line 1\nline 2\n");

        var logs = await _lifecycle.GetLogsAsync(AgentAId, 50, ct);

        logs.ShouldBe("line 1\nline 2\n");
    }
}
