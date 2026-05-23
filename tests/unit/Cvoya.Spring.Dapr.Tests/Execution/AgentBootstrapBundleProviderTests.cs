// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Tests.Execution;

using Cvoya.Spring.Connectors;
using Cvoya.Spring.Core.Catalog;
using Cvoya.Spring.Core.Execution;
using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Core.Tenancy;
using Cvoya.Spring.Dapr.Connectors;
using Cvoya.Spring.Dapr.Execution;
using Cvoya.Spring.Dapr.Mcp;

using Microsoft.Extensions.Options;

using NSubstitute;

using Shouldly;

using Xunit;

/// <summary>
/// Unit tests for <see cref="AgentBootstrapBundleProvider"/>. ADR-0055 Wave 3
/// scope: the launcher's per-runtime contribution + the agent-definition
/// YAML + tenant-config JSON + the connector runtime-context contribution.
/// The previously hardcoded <c>CLAUDE.md</c> file is now sourced from the
/// launcher's <see cref="IAgentRuntimeLauncher.ContributeBundleAsync"/>.
/// </summary>
public class AgentBootstrapBundleProviderTests
{
    private const string AgentId = "11111111111111111111111111111111";
    private const string McpContainerHost = "host.docker.internal";
    private const int McpPort = 5050;

    private readonly IAgentDefinitionProvider _agentDefinitionProvider =
        Substitute.For<IAgentDefinitionProvider>();
    private readonly IRuntimeCatalog _runtimeCatalog = Substitute.For<IRuntimeCatalog>();
    private readonly IConnectorRuntimeContextResolver _connectorContextResolver =
        Substitute.For<IConnectorRuntimeContextResolver>();
    private readonly ITenantContext _tenantContext = Substitute.For<ITenantContext>();
    private readonly TimeProvider _timeProvider = Substitute.For<TimeProvider>();
    private readonly StubLauncher _launcher = new();

    private readonly AgentBootstrapBundleProvider _provider;

    public AgentBootstrapBundleProviderTests()
    {
        _tenantContext.CurrentTenantId.Returns(Guid.Parse("22222222-2222-2222-2222-222222222222"));
        _timeProvider.GetUtcNow().Returns(new DateTimeOffset(2026, 5, 22, 12, 0, 0, TimeSpan.Zero));

        // The catalogue maps the runtime id ("claude-code") to the
        // launcher strategy id ("claude-code-cli") so the bundle provider
        // can pick a launcher.
        _runtimeCatalog.GetAgentRuntime("claude-code").Returns(new AgentRuntime(
            Id: "claude-code",
            DisplayName: "Claude Code",
            DefaultImage: "ghcr.io/test/claude:latest",
            Launcher: "claude-code-cli",
            ThreadBinding: new ThreadBinding(ThreadBindingKind.CliArg, ArgName: "--resume"),
            SystemPromptInjection: new SystemPromptInjection(SystemPromptInjectionKind.File, FilePath: "CLAUDE.md"),
            ModelProviders: Array.Empty<AgentRuntimeProviderEdge>()));

        // Default: no connector contribution.
        _connectorContextResolver
            .ResolveAsync(Arg.Any<Address>(), Arg.Any<CancellationToken>())
            .Returns(ConnectorRuntimeContextContribution.Empty);

        _provider = new AgentBootstrapBundleProvider(
            _agentDefinitionProvider,
            new AgentDefinitionSerializer(_runtimeCatalog),
            _runtimeCatalog,
            new[] { (IAgentRuntimeLauncher)_launcher },
            _connectorContextResolver,
            Options.Create(new McpServerOptions
            {
                ContainerHost = McpContainerHost,
                Port = McpPort,
            }),
            _tenantContext,
            _timeProvider);
    }

    [Fact]
    public async Task BuildAsync_ReturnsNullForUnknownAgent()
    {
        _agentDefinitionProvider.GetByIdAsync("missing", Arg.Any<CancellationToken>())
            .Returns((AgentDefinition?)null);

        var result = await _provider.BuildAsync("missing", TestContext.Current.CancellationToken);

        result.ShouldBeNull();
    }

    [Fact]
    public async Task BuildAsync_IncludesLauncherContributionAndStaticContextFiles()
    {
        StubAgent(instructions: "You are a helpful agent.");

        var bundle = await _provider.BuildAsync(AgentId, TestContext.Current.CancellationToken);

        bundle.ShouldNotBeNull();
        // The bundle must carry the agent-definition YAML and tenant-config
        // JSON the in-container SDK reads under /context/, plus whatever
        // the launcher contributes for its runtime.
        bundle!.Files.ShouldContain(f => f.Path == AgentBootstrapBundleProvider.AgentDefinitionPath);
        bundle.Files.ShouldContain(f => f.Path == AgentBootstrapBundleProvider.TenantConfigPath);
        bundle.Files.ShouldContain(f => f.Path == "CLAUDE.md");
        bundle.Files.ShouldContain(f => f.Path == ".mcp.json");
    }

    [Fact]
    public async Task BuildAsync_LauncherContribution_CarriesAgentInstructions()
    {
        StubAgent(instructions: "You are a helpful agent.");

        var bundle = await _provider.BuildAsync(AgentId, TestContext.Current.CancellationToken);

        var claudeMd = bundle!.Files.First(f => f.Path == "CLAUDE.md");
        claudeMd.Content.ShouldBe("You are a helpful agent.");
    }

    [Fact]
    public async Task BuildAsync_LauncherContribution_HandlesNullInstructions()
    {
        // AgentDefinition.Instructions is nullable; the launcher's
        // contribution must materialise an empty CLAUDE.md rather than
        // throwing — verified at the provider level since the provider
        // owns the launcher-selection seam.
        StubAgent(instructions: null);

        var bundle = await _provider.BuildAsync(AgentId, TestContext.Current.CancellationToken);

        var claudeMd = bundle!.Files.First(f => f.Path == "CLAUDE.md");
        claudeMd.Content.ShouldBe(string.Empty);
    }

    [Fact]
    public async Task BuildAsync_PlatformFileHashes_PinLauncherDeclaredPaths()
    {
        // Wave 3: the launcher names its platform-authoritative paths via
        // AgentBootstrapContribution.PlatformFilePaths. The provider hashes
        // each named file and exposes the mapping the sidecar's integrity
        // check keys on.
        StubAgent(instructions: "x");

        var bundle = await _provider.BuildAsync(AgentId, TestContext.Current.CancellationToken);

        bundle!.PlatformFileHashes.Keys.ShouldBe(new[] { "CLAUDE.md", ".mcp.json" }, ignoreOrder: true);
        bundle.PlatformFileHashes["CLAUDE.md"]
            .ShouldBe(bundle.Files.First(f => f.Path == "CLAUDE.md").Sha256);
        bundle.PlatformFileHashes[".mcp.json"]
            .ShouldBe(bundle.Files.First(f => f.Path == ".mcp.json").Sha256);
    }

    [Fact]
    public async Task BuildAsync_FilesAreSortedByPath()
    {
        // Hash determinism (AgentBootstrapBundleHasher) requires sorted input.
        StubAgent(instructions: "x");

        var bundle = await _provider.BuildAsync(AgentId, TestContext.Current.CancellationToken);

        var paths = bundle!.Files.Select(f => f.Path).ToArray();
        paths.ShouldBe(paths.OrderBy(p => p, StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public async Task BuildAsync_Version_IsContentAddressableAndDeterministic()
    {
        // Same inputs → same version, across two provider builds and even
        // across two clock ticks (issuedAt does not participate in the hash).
        StubAgent(instructions: "stable instructions");

        var first = await _provider.BuildAsync(AgentId, TestContext.Current.CancellationToken);

        // Advance the clock — the hash must not change.
        _timeProvider.GetUtcNow().Returns(new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var second = await _provider.BuildAsync(AgentId, TestContext.Current.CancellationToken);

        first!.Version.ShouldBe(second!.Version);
        first.Version.ShouldStartWith("sha256:");
    }

    [Fact]
    public async Task BuildAsync_Version_ChangesWhenInstructionsChange()
    {
        StubAgent(instructions: "version A");
        var bundleA = await _provider.BuildAsync(AgentId, TestContext.Current.CancellationToken);

        StubAgent(instructions: "version B");
        var bundleB = await _provider.BuildAsync(AgentId, TestContext.Current.CancellationToken);

        bundleA!.Version.ShouldNotBe(bundleB!.Version);
    }

    [Fact]
    public async Task BuildAsync_IssuedAt_TracksTimeProvider()
    {
        // issuedAt is the only field that should reflect wallclock; sourced
        // from the injected TimeProvider so tests can pin it.
        var t = new DateTimeOffset(2026, 5, 22, 12, 0, 0, TimeSpan.Zero);
        _timeProvider.GetUtcNow().Returns(t);
        StubAgent(instructions: "x");

        var bundle = await _provider.BuildAsync(AgentId, TestContext.Current.CancellationToken);

        bundle!.IssuedAt.ShouldBe(t);
    }

    [Fact]
    public async Task BuildAsync_TenantConfig_CarriesCurrentTenantId()
    {
        var tenantId = Guid.Parse("33333333-3333-3333-3333-333333333333");
        _tenantContext.CurrentTenantId.Returns(tenantId);
        StubAgent(instructions: "x");

        var bundle = await _provider.BuildAsync(AgentId, TestContext.Current.CancellationToken);

        var tenantConfig = bundle!.Files.First(f => f.Path == AgentBootstrapBundleProvider.TenantConfigPath);
        tenantConfig.Content.ShouldContain(tenantId.ToString("N"));
    }

    [Fact]
    public async Task BuildAsync_FileSha256_MatchesContentHash()
    {
        StubAgent(instructions: "deterministic");

        var bundle = await _provider.BuildAsync(AgentId, TestContext.Current.CancellationToken);

        foreach (var file in bundle!.Files)
        {
            file.Sha256.ShouldBe(AgentBootstrapBundleHasher.ComputeFileHash(file.Content));
        }
    }

    [Fact]
    public async Task BuildAsync_MergesConnectorContextFiles()
    {
        // ADR-0055: connector per-binding files now ride the bundle rather
        // than the launcher spec. The provider must fold the resolver's
        // file contribution into the bundle's Files set.
        StubAgent(instructions: "x");
        _connectorContextResolver
            .ResolveAsync(Arg.Any<Address>(), Arg.Any<CancellationToken>())
            .Returns(new ConnectorRuntimeContextContribution(
                new Dictionary<string, string>(),
                new Dictionary<string, string>
                {
                    ["connectors/github/binding.json"] = "{}",
                }));

        var bundle = await _provider.BuildAsync(AgentId, TestContext.Current.CancellationToken);

        bundle!.Files.ShouldContain(f => f.Path == "connectors/github/binding.json");
    }

    private void StubAgent(string? instructions)
    {
        _agentDefinitionProvider.GetByIdAsync(AgentId, Arg.Any<CancellationToken>())
            .Returns(new AgentDefinition(
                AgentId: AgentId,
                Name: "Test Agent",
                Instructions: instructions,
                Execution: new AgentExecutionConfig(
                    Runtime: "claude-code",
                    Image: "ghcr.io/example/test-agent:latest")));
    }

    /// <summary>
    /// Minimal launcher stub. Returns a bundle contribution mirroring the
    /// production ClaudeCodeLauncher shape (CLAUDE.md + .mcp.json with the
    /// instructions blob and an empty Authorization placeholder), without
    /// pulling in the real launcher's credential-resolution dependencies.
    /// </summary>
    private sealed class StubLauncher : IAgentRuntimeLauncher
    {
        public string Kind => "claude-code-cli";

        public Task<AgentLaunchSpec> PrepareAsync(
            AgentLaunchContext context,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new AgentLaunchSpec(
                EnvironmentVariables: new Dictionary<string, string>()));

        public Task<AgentBootstrapContribution> ContributeBundleAsync(
            AgentBootstrapContributionContext context,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new AgentBootstrapContribution(
                Files: new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["CLAUDE.md"] = context.Definition.Instructions ?? string.Empty,
                    [".mcp.json"] = "{\"mcpServers\":{\"spring-voyage\":{\"url\":\""
                        + context.McpEndpoint
                        + "\",\"headers\":{\"Authorization\":\"Bearer \"}}}}",
                },
                PlatformFilePaths: new[] { "CLAUDE.md", ".mcp.json" }));

        public IReadOnlyList<Cvoya.Spring.Core.ModelProviders.ProbeStep> GetProbeSteps(
            Cvoya.Spring.Core.ModelProviders.ModelProviderInstallConfig config,
            string credential)
            => Array.Empty<Cvoya.Spring.Core.ModelProviders.ProbeStep>();
    }
}
