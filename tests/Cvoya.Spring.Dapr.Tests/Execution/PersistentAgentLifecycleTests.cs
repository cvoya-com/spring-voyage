// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Tests.Execution;

using Cvoya.Spring.Core;
using Cvoya.Spring.Core.Catalog;
using Cvoya.Spring.Core.Execution;
using Cvoya.Spring.Core.ModelProviders;
using Cvoya.Spring.Dapr.Execution;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using NSubstitute;

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
        services.AddSingleton<PersistentAgentRegistry>();
        services.AddSingleton<PersistentAgentLifecycle>();
        var sp = services.BuildServiceProvider();
        _registry = sp.GetRequiredService<PersistentAgentRegistry>();
        _lifecycle = sp.GetRequiredService<PersistentAgentLifecycle>();
    }

    [Fact]
    public async Task Deploy_WhenAgentMissing_Throws()
    {
        var ct = TestContext.Current.CancellationToken;
        _agentProvider.GetByIdAsync("ghost", Arg.Any<CancellationToken>())
            .Returns((AgentDefinition?)null);

        var ex = await Should.ThrowAsync<SpringException>(
            () => _lifecycle.DeployAsync("ghost", cancellationToken: ct));

        ex.Message.ShouldContain("ghost");
    }

    [Fact]
    public async Task Deploy_WhenExecutionMissing_Throws()
    {
        var ct = TestContext.Current.CancellationToken;
        _agentProvider.GetByIdAsync("a", Arg.Any<CancellationToken>())
            .Returns(new AgentDefinition("a", "A", null, Execution: null));

        var ex = await Should.ThrowAsync<SpringException>(
            () => _lifecycle.DeployAsync("a", cancellationToken: ct));

        ex.Message.ShouldContain("execution");
    }

    [Fact]
    public async Task Deploy_WhenAgentIsEphemeral_Throws()
    {
        var ct = TestContext.Current.CancellationToken;
        _agentProvider.GetByIdAsync("e", Arg.Any<CancellationToken>())
            .Returns(new AgentDefinition(
                "e",
                "E",
                null,
                new AgentExecutionConfig("claude", "img", Hosting: AgentHostingMode.Ephemeral)));

        var ex = await Should.ThrowAsync<SpringException>(
            () => _lifecycle.DeployAsync("e", cancellationToken: ct));

        ex.Message.ShouldContain("persistent");
    }

    [Fact]
    public async Task Deploy_WhenDefinitionMissingImageAndNoOverride_Throws()
    {
        var ct = TestContext.Current.CancellationToken;
        _agentProvider.GetByIdAsync("a", Arg.Any<CancellationToken>())
            .Returns(new AgentDefinition(
                "a",
                "A",
                null,
                new AgentExecutionConfig("claude", Image: null, Hosting: AgentHostingMode.Persistent)));

        var ex = await Should.ThrowAsync<SpringException>(
            () => _lifecycle.DeployAsync("a", cancellationToken: ct));

        ex.Message.ShouldContain("image");
    }

    [Fact]
    public async Task Deploy_WhenAlreadyHealthy_ReturnsExistingWithoutStartingContainer()
    {
        var ct = TestContext.Current.CancellationToken;
        var endpoint = new Uri("http://localhost:8999/");
        _registry.Register(
            "a",
            endpoint,
            "container-abc",
            new AgentDefinition(
                "a",
                "A",
                null,
                new AgentExecutionConfig("claude", "img", Hosting: AgentHostingMode.Persistent)));

        _agentProvider.GetByIdAsync("a", Arg.Any<CancellationToken>())
            .Returns(new AgentDefinition(
                "a",
                "A",
                null,
                new AgentExecutionConfig("claude", "img", Hosting: AgentHostingMode.Persistent)));

        var result = await _lifecycle.DeployAsync("a", cancellationToken: ct);

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
        var result = await _lifecycle.UndeployAsync("unknown", ct);
        result.ShouldBeFalse();
    }

    [Fact]
    public async Task Undeploy_WhenAgentRegistered_StopsContainerAndReturnsTrue()
    {
        var ct = TestContext.Current.CancellationToken;
        var endpoint = new Uri("http://localhost:8999/");
        _registry.Register("a", endpoint, "container-abc", definition: null);

        var result = await _lifecycle.UndeployAsync("a", ct);

        result.ShouldBeTrue();
        await _containerRuntime.Received()
            .StopAsync("container-abc", Arg.Any<CancellationToken>());
        _registry.TryGet("a", out var entry).ShouldBeFalse();
        entry.ShouldBeNull();
    }

    [Fact]
    public async Task Scale_WithZeroReplicas_Undeploys()
    {
        var ct = TestContext.Current.CancellationToken;
        var endpoint = new Uri("http://localhost:8999/");
        _registry.Register("a", endpoint, "container-abc", definition: null);

        await _lifecycle.ScaleAsync("a", 0, ct);

        _registry.TryGet("a", out _).ShouldBeFalse();
    }

    [Fact]
    public async Task Scale_WithMoreThanOneReplica_Throws()
    {
        var ct = TestContext.Current.CancellationToken;
        var ex = await Should.ThrowAsync<SpringException>(
            () => _lifecycle.ScaleAsync("a", 2, ct));

        ex.Message.ShouldContain("Horizontal scaling");
    }

    [Fact]
    public async Task GetLogs_WhenAgentNotDeployed_Throws()
    {
        var ct = TestContext.Current.CancellationToken;
        var ex = await Should.ThrowAsync<SpringException>(
            () => _lifecycle.GetLogsAsync("ghost", cancellationToken: ct));

        ex.Message.ShouldContain("not deployed");
    }

    [Fact]
    public async Task GetLogs_ForwardsToContainerRuntime()
    {
        var ct = TestContext.Current.CancellationToken;
        var endpoint = new Uri("http://localhost:8999/");
        _registry.Register("a", endpoint, "container-abc", definition: null);

        _containerRuntime
            .GetLogsAsync("container-abc", 50, Arg.Any<CancellationToken>())
            .Returns("line 1\nline 2\n");

        var logs = await _lifecycle.GetLogsAsync("a", 50, ct);

        logs.ShouldBe("line 1\nline 2\n");
    }
}