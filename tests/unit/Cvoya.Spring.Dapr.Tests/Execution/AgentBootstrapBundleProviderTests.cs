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

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

using NSubstitute;

using Shouldly;

using Xunit;

/// <summary>
/// Unit tests for <see cref="AgentBootstrapBundleProvider"/>. ADR-0055 Wave 3
/// scope: the launcher's per-runtime contribution + the agent-definition
/// YAML + tenant-config JSON + the connector runtime-context contribution.
/// The previously hardcoded <c>CLAUDE.md</c> file is now sourced from the
/// launcher's <see cref="IAgentRuntimeLauncher.ContributeBundleAsync"/>,
/// which receives the assembler-built system prompt on the contribution
/// context. Per #2672 the Claude launcher's prompt file moved off the
/// CLI's auto-discovered <c>CLAUDE.md</c> to <c>.spring/system-prompt.md</c>
/// under the ADR-0058 §2.2.2 namespace; the stub launcher mirrors the
/// production filename so the bundle-shape tests reflect the real wire.
/// </summary>
public class AgentBootstrapBundleProviderTests
{
    private const string AgentId = "11111111111111111111111111111111";
    private const string McpContainerHost = "host.docker.internal";
    private const int McpPort = 5050;
    private const string AssembledPromptText = "ASSEMBLED SYSTEM PROMPT";

    private readonly IAgentDefinitionProvider _agentDefinitionProvider =
        Substitute.For<IAgentDefinitionProvider>();
    private readonly IRuntimeCatalog _runtimeCatalog = Substitute.For<IRuntimeCatalog>();
    private readonly IConnectorRuntimeContextResolver _connectorContextResolver =
        Substitute.For<IConnectorRuntimeContextResolver>();
    private readonly IConnectorPromptContextResolver _connectorPromptContextResolver =
        Substitute.For<IConnectorPromptContextResolver>();
    private readonly IIdentityPromptContextResolver _identityPromptContextResolver =
        Substitute.For<IIdentityPromptContextResolver>();
    private readonly IPromptAssembler _promptAssembler = Substitute.For<IPromptAssembler>();
    private readonly IServiceScopeFactory _scopeFactory = Substitute.For<IServiceScopeFactory>();
    private readonly TimeProvider _timeProvider = Substitute.For<TimeProvider>();
    private readonly StubLauncher _launcher = new();
    private string _assembledPrompt = AssembledPromptText;

    private readonly AgentBootstrapBundleProvider _provider;

    public AgentBootstrapBundleProviderTests()
    {
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
            ModelProviders: Array.Empty<AgentRuntimeProviderEdge>()));

        // Default: no connector contribution (both kinds).
        _connectorContextResolver
            .ResolveAsync(Arg.Any<Address>(), Arg.Any<CancellationToken>())
            .Returns(ConnectorRuntimeContextContribution.Empty);
        _connectorPromptContextResolver
            .ResolveAsync(Arg.Any<Address>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<string>());
        _identityPromptContextResolver
            .ResolveAsync(Arg.Any<Address>(), Arg.Any<CancellationToken>())
            .Returns((string?)null);

        // Stub the assembler so each test can pin / vary the resulting
        // system prompt and assert it lands in the bundle's
        // `.spring/system-prompt.md` (ADR-0058 §2.2.2 / #2672).
        _promptAssembler
            .AssembleAsync(Arg.Any<PromptAssemblyContext?>(), Arg.Any<CancellationToken>())
            .Returns(_ => _assembledPrompt);

        // The scope factory + skill-registry sequence is exercised by
        // EquippedBundleLoader, which the production provider invokes.
        // For these unit tests we wire an empty service scope so the
        // loader returns no bundles — the test focus is the file
        // contribution composition, not equipped-bundle inheritance
        // (which is covered by EquippedSkillsLayer2InheritanceTests and
        // EquippedSkillsLayer4Tests).
        var emptyServices = new ServiceCollection().BuildServiceProvider();
        var scope = Substitute.For<IServiceScope>();
        scope.ServiceProvider.Returns(emptyServices);
        _scopeFactory.CreateScope().Returns(scope);

        _provider = new AgentBootstrapBundleProvider(
            _agentDefinitionProvider,
            _runtimeCatalog,
            new[] { (IAgentRuntimeLauncher)_launcher },
            _connectorContextResolver,
            _connectorPromptContextResolver,
            _identityPromptContextResolver,
            _promptAssembler,
            _scopeFactory,
            Options.Create(new McpServerOptions
            {
                ContainerHost = McpContainerHost,
                Port = McpPort,
            }),
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
        // The bundle must carry whatever the launcher contributes for its
        // runtime — the assembled system prompt and the MCP config file.
        bundle!.Files.ShouldContain(f => f.Path == ".spring/system-prompt.md");
        bundle.Files.ShouldContain(f => f.Path == ".mcp.json");
    }

    [Fact]
    public async Task BuildAsync_LauncherContribution_CarriesAssembledSystemPrompt()
    {
        // After the silent-dispatch cutover the provider invokes the
        // assembler once per bundle build and passes the result to the
        // launcher contribution. The bundle's
        // `.spring/system-prompt.md` is that string — NOT
        // Definition.Instructions (#2672 moved it off `CLAUDE.md`).
        _assembledPrompt = "MULTI-LAYER ASSEMBLED PROMPT";
        StubAgent(instructions: "You are a helpful agent.");

        var bundle = await _provider.BuildAsync(AgentId, TestContext.Current.CancellationToken);

        var promptFile = bundle!.Files.First(f => f.Path == ".spring/system-prompt.md");
        promptFile.Content.ShouldBe("MULTI-LAYER ASSEMBLED PROMPT");
    }

    [Fact]
    public async Task BuildAsync_LauncherContribution_HandlesEmptyAssembledPrompt()
    {
        // Defensive: a synthetic empty assembled prompt must materialise
        // an empty `.spring/system-prompt.md` rather than throwing.
        _assembledPrompt = string.Empty;
        StubAgent(instructions: null);

        var bundle = await _provider.BuildAsync(AgentId, TestContext.Current.CancellationToken);

        var promptFile = bundle!.Files.First(f => f.Path == ".spring/system-prompt.md");
        promptFile.Content.ShouldBe(string.Empty);
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

        bundle!.PlatformFileHashes.Keys.ShouldBe(new[] { ".spring/system-prompt.md", ".mcp.json" }, ignoreOrder: true);
        bundle.PlatformFileHashes[".spring/system-prompt.md"]
            .ShouldBe(bundle.Files.First(f => f.Path == ".spring/system-prompt.md").Sha256);
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
    public async Task BuildAsync_Version_ChangesWhenAssembledPromptChanges()
    {
        StubAgent(instructions: "instructions");
        _assembledPrompt = "version A";
        var bundleA = await _provider.BuildAsync(AgentId, TestContext.Current.CancellationToken);

        _assembledPrompt = "version B";
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
    public async Task BuildAsync_ConcurrentThreadsTrue_FoldsGuardIntoAssembledSystemPrompt()
    {
        // #2668: the CLI launchers no longer prepend the
        // ConcurrentThreadsGuard to the assembled prompt themselves.
        // The guard now travels via the launcher's system-prompt file
        // — `.spring/system-prompt.md` for Claude/Codex/Gemini under
        // ADR-0058 §2.2.2 (Gemini may instead write `GEMINI.md` when
        // its system_prompt_mode opts for the legacy filename). The
        // bundle provider folds the guard into AssembledSystemPrompt
        // before the launcher's ContributeBundleAsync receives it.
        _assembledPrompt = "USER ASSEMBLED PROMPT";
        StubAgent(instructions: "x", concurrentThreads: true);

        var bundle = await _provider.BuildAsync(AgentId, TestContext.Current.CancellationToken);

        var promptFile = bundle!.Files.First(f => f.Path == ".spring/system-prompt.md");
        promptFile.Content.ShouldStartWith("## Spring Voyage runtime guard — concurrent_threads is on");
        promptFile.Content.ShouldContain("USER ASSEMBLED PROMPT");
        promptFile.Content.IndexOf("concurrent_threads is on", StringComparison.Ordinal)
            .ShouldBeLessThan(promptFile.Content.IndexOf("USER ASSEMBLED PROMPT", StringComparison.Ordinal));
    }

    [Fact]
    public async Task BuildAsync_ConcurrentThreadsFalse_LeavesAssembledSystemPromptUnchanged()
    {
        // Mirror of the guard-on test: when concurrent_threads is
        // explicitly off the guard is NOT folded in. The bundle's
        // prompt file is the assembler's output verbatim.
        _assembledPrompt = "USER ASSEMBLED PROMPT";
        StubAgent(instructions: "x", concurrentThreads: false);

        var bundle = await _provider.BuildAsync(AgentId, TestContext.Current.CancellationToken);

        var promptFile = bundle!.Files.First(f => f.Path == ".spring/system-prompt.md");
        promptFile.Content.ShouldBe("USER ASSEMBLED PROMPT");
        promptFile.Content.ShouldNotContain("concurrent_threads is on");
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
                    [".spring/connectors/github/binding.json"] = "{}",
                }));

        var bundle = await _provider.BuildAsync(AgentId, TestContext.Current.CancellationToken);

        bundle!.Files.ShouldContain(f => f.Path == ".spring/connectors/github/binding.json");
    }

    // Default ConcurrentThreads to false in the test fixture so the
    // baseline-coverage tests (file presence, sorted ordering,
    // version determinism, etc.) see the assembler output verbatim
    // without the guard prepended. Tests that exercise the guard
    // fold path (#2668) opt into concurrentThreads: true explicitly.
    private void StubAgent(string? instructions, bool concurrentThreads = false)
    {
        _agentDefinitionProvider.GetByIdAsync(AgentId, Arg.Any<CancellationToken>())
            .Returns(new AgentDefinition(
                AgentId: AgentId,
                Name: "Test Agent",
                Instructions: instructions,
                Execution: new AgentExecutionConfig(
                    Runtime: "claude-code",
                    Image: "ghcr.io/example/test-agent:latest",
                    ConcurrentThreads: concurrentThreads)));
    }

    /// <summary>
    /// Minimal launcher stub. Returns a bundle contribution mirroring the
    /// production ClaudeCodeLauncher shape post-#2672 — the assembled
    /// system prompt lives at <c>.spring/system-prompt.md</c> under the
    /// ADR-0058 §2.2.2 namespace (off the CLI's auto-discovered prompt
    /// filename), and <c>.mcp.json</c> carries an empty Authorization
    /// placeholder.
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
                    [".spring/system-prompt.md"] = context.AssembledSystemPrompt,
                    [".mcp.json"] = "{\"mcpServers\":{\"spring-voyage\":{\"url\":\""
                        + context.McpEndpoint
                        + "\",\"headers\":{\"Authorization\":\"Bearer \"}}}}",
                },
                PlatformFilePaths: new[] { ".spring/system-prompt.md", ".mcp.json" }));

        public string? GetWorkspacePromptFragment() => null;

        public IReadOnlyList<Cvoya.Spring.Core.ModelProviders.ProbeStep> GetProbeSteps(
            Cvoya.Spring.Core.ModelProviders.ModelProviderInstallConfig config,
            string credential)
            => Array.Empty<Cvoya.Spring.Core.ModelProviders.ProbeStep>();
    }
}
