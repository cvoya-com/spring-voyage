// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Tests.Execution;

using Cvoya.Spring.Core.Catalog;
using Cvoya.Spring.Core.Execution;
using Cvoya.Spring.Core.Tenancy;
using Cvoya.Spring.Dapr.Execution;

using NSubstitute;

using Shouldly;

using Xunit;

/// <summary>
/// Unit tests for <see cref="AgentBootstrapBundleProvider"/>. ADR-0055 Wave 1
/// scope: static <c>CLAUDE.md</c> + <c>context/agent-definition.yaml</c> +
/// <c>context/tenant-config.json</c>; <c>.mcp.json</c> joins the bundle in
/// Wave 3 when launchers stop emitting it inline.
/// </summary>
public class AgentBootstrapBundleProviderTests
{
    private const string AgentId = "11111111111111111111111111111111";

    private readonly IAgentDefinitionProvider _agentDefinitionProvider =
        Substitute.For<IAgentDefinitionProvider>();
    private readonly IRuntimeCatalog _runtimeCatalog = Substitute.For<IRuntimeCatalog>();
    private readonly ITenantContext _tenantContext = Substitute.For<ITenantContext>();
    private readonly TimeProvider _timeProvider = Substitute.For<TimeProvider>();

    private readonly AgentBootstrapBundleProvider _provider;

    public AgentBootstrapBundleProviderTests()
    {
        _tenantContext.CurrentTenantId.Returns(Guid.Parse("22222222-2222-2222-2222-222222222222"));
        _timeProvider.GetUtcNow().Returns(new DateTimeOffset(2026, 5, 22, 12, 0, 0, TimeSpan.Zero));
        _provider = new AgentBootstrapBundleProvider(
            _agentDefinitionProvider,
            new AgentDefinitionSerializer(_runtimeCatalog),
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
    public async Task BuildAsync_IncludesAllStaticFilesInWave1()
    {
        StubAgent(instructions: "You are a helpful agent.");

        var bundle = await _provider.BuildAsync(AgentId, TestContext.Current.CancellationToken);

        bundle.ShouldNotBeNull();
        bundle.Files.Count.ShouldBe(3);
        bundle.Files.ShouldContain(f => f.Path == AgentBootstrapBundleProvider.ClaudeMdPath);
        bundle.Files.ShouldContain(f => f.Path == AgentBootstrapBundleProvider.AgentDefinitionPath);
        bundle.Files.ShouldContain(f => f.Path == AgentBootstrapBundleProvider.TenantConfigPath);
    }

    [Fact]
    public async Task BuildAsync_ClaudeMd_CarriesAgentInstructions()
    {
        StubAgent(instructions: "You are a helpful agent.");

        var bundle = await _provider.BuildAsync(AgentId, TestContext.Current.CancellationToken);

        var claudeMd = bundle!.Files.First(f => f.Path == AgentBootstrapBundleProvider.ClaudeMdPath);
        claudeMd.Content.ShouldBe("You are a helpful agent.");
    }

    [Fact]
    public async Task BuildAsync_ClaudeMd_HandlesNullInstructions()
    {
        // AgentDefinition.Instructions is nullable; the bundle must materialise
        // an empty CLAUDE.md rather than throwing.
        StubAgent(instructions: null);

        var bundle = await _provider.BuildAsync(AgentId, TestContext.Current.CancellationToken);

        var claudeMd = bundle!.Files.First(f => f.Path == AgentBootstrapBundleProvider.ClaudeMdPath);
        claudeMd.Content.ShouldBe(string.Empty);
    }

    [Fact]
    public async Task BuildAsync_PlatformFileHashes_PinClaudeMdOnly_InWave1()
    {
        // Wave 1: only CLAUDE.md is platform-authoritative. .mcp.json
        // joins the pinned set in Wave 3.
        StubAgent(instructions: "x");

        var bundle = await _provider.BuildAsync(AgentId, TestContext.Current.CancellationToken);

        bundle!.PlatformFileHashes.Keys.ShouldBe(new[] { AgentBootstrapBundleProvider.ClaudeMdPath });
        bundle.PlatformFileHashes[AgentBootstrapBundleProvider.ClaudeMdPath]
            .ShouldBe(bundle.Files.First(f => f.Path == AgentBootstrapBundleProvider.ClaudeMdPath).Sha256);
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
}
