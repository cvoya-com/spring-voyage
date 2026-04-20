// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Tests.AgentRuntimes;

using Cvoya.Spring.Core.AgentRuntimes;
using Cvoya.Spring.Dapr.AgentRuntimes;
using Cvoya.Spring.Dapr.Data;
using Cvoya.Spring.Dapr.Tenancy;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

using NSubstitute;

using Shouldly;

using Xunit;

/// <summary>
/// Unit tests for <see cref="TenantAgentRuntimeInstallService"/> —
/// install/uninstall/list/get round-trips, tenant isolation, and
/// config-materialisation from seed defaults.
/// </summary>
public class TenantAgentRuntimeInstallServiceTests
{
    private const string TenantA = "tenant-a";
    private const string TenantB = "tenant-b";

    [Fact]
    public async Task InstallAsync_NoConfig_MaterialisesFromRuntimeDefaults()
    {
        var ct = TestContext.Current.CancellationToken;
        var (context, registry) = CreateContextAndRegistry(TenantA);
        var sut = CreateSut(context, TenantA, registry);

        var result = await sut.InstallAsync("claude", config: null, ct);

        result.RuntimeId.ShouldBe("claude");
        result.TenantId.ShouldBe(TenantA);
        result.Config.Models.ShouldBe(new[] { "claude-sonnet-4-5", "claude-opus-4-1" });
        result.Config.DefaultModel.ShouldBe("claude-sonnet-4-5");
        result.Config.BaseUrl.ShouldBeNull();
    }

    [Fact]
    public async Task InstallAsync_WithConfig_PersistsProvidedConfig()
    {
        var ct = TestContext.Current.CancellationToken;
        var (context, registry) = CreateContextAndRegistry(TenantA);
        var sut = CreateSut(context, TenantA, registry);

        var explicitConfig = new AgentRuntimeInstallConfig(
            Models: new[] { "claude-opus-4-1" },
            DefaultModel: "claude-opus-4-1",
            BaseUrl: "https://proxy.example.com");

        var result = await sut.InstallAsync("claude", explicitConfig, ct);

        result.Config.Models.ShouldBe(new[] { "claude-opus-4-1" });
        result.Config.DefaultModel.ShouldBe("claude-opus-4-1");
        result.Config.BaseUrl.ShouldBe("https://proxy.example.com");
    }

    [Fact]
    public async Task InstallAsync_Repeat_PreservesExistingConfigWhenCallerPassesNull()
    {
        // Idempotency contract: re-running install without a body must
        // not overwrite operator-edited config (matches the
        // ITenantSeedProvider rule).
        var ct = TestContext.Current.CancellationToken;
        var (context, registry) = CreateContextAndRegistry(TenantA);
        var sut = CreateSut(context, TenantA, registry);

        var operatorEdit = new AgentRuntimeInstallConfig(
            Models: new[] { "claude-opus-4-1" }, DefaultModel: "claude-opus-4-1", BaseUrl: null);
        await sut.InstallAsync("claude", operatorEdit, ct);

        var refreshed = await sut.InstallAsync("claude", config: null, ct);

        refreshed.Config.Models.ShouldBe(new[] { "claude-opus-4-1" });
        refreshed.Config.DefaultModel.ShouldBe("claude-opus-4-1");
    }

    [Fact]
    public async Task InstallAsync_UnknownRuntime_Throws()
    {
        var ct = TestContext.Current.CancellationToken;
        var (context, registry) = CreateContextAndRegistry(TenantA);
        var sut = CreateSut(context, TenantA, registry);

        await Should.ThrowAsync<InvalidOperationException>(
            async () => await sut.InstallAsync("unknown", config: null, ct));
    }

    [Fact]
    public async Task UninstallAsync_SoftDeletesRow_HiddenFromList()
    {
        var ct = TestContext.Current.CancellationToken;
        var (context, registry) = CreateContextAndRegistry(TenantA);
        var sut = CreateSut(context, TenantA, registry);

        await sut.InstallAsync("claude", config: null, ct);
        await sut.UninstallAsync("claude", ct);

        var list = await sut.ListAsync(ct);
        list.ShouldBeEmpty();
    }

    [Fact]
    public async Task InstallAsync_AfterUninstall_RevivesRow()
    {
        var ct = TestContext.Current.CancellationToken;
        var (context, registry) = CreateContextAndRegistry(TenantA);
        var sut = CreateSut(context, TenantA, registry);

        await sut.InstallAsync("claude", config: null, ct);
        await sut.UninstallAsync("claude", ct);
        var revived = await sut.InstallAsync("claude", config: null, ct);

        revived.ShouldNotBeNull();
        var list = await sut.ListAsync(ct);
        list.Count.ShouldBe(1);
        list[0].RuntimeId.ShouldBe("claude");
    }

    [Fact]
    public async Task GetAsync_NotInstalled_ReturnsNull()
    {
        var ct = TestContext.Current.CancellationToken;
        var (context, registry) = CreateContextAndRegistry(TenantA);
        var sut = CreateSut(context, TenantA, registry);

        (await sut.GetAsync("claude", ct)).ShouldBeNull();
    }

    [Fact]
    public async Task UpdateConfigAsync_NotInstalled_Throws()
    {
        var ct = TestContext.Current.CancellationToken;
        var (context, registry) = CreateContextAndRegistry(TenantA);
        var sut = CreateSut(context, TenantA, registry);

        await Should.ThrowAsync<InvalidOperationException>(
            async () => await sut.UpdateConfigAsync(
                "claude",
                AgentRuntimeInstallConfig.Empty,
                ct));
    }

    [Fact]
    public async Task ListAsync_HonoursTenantIsolation()
    {
        var ct = TestContext.Current.CancellationToken;
        var dbName = Guid.NewGuid().ToString();

        var tenantAContext = CreateDbContext(dbName, TenantA);
        var tenantBContext = CreateDbContext(dbName, TenantB);
        var registry = CreateRegistry();

        var tenantAService = CreateSut(tenantAContext, TenantA, registry);
        var tenantBService = CreateSut(tenantBContext, TenantB, registry);

        await tenantAService.InstallAsync("claude", config: null, ct);
        await tenantBService.InstallAsync("openai", config: null, ct);

        var tenantAList = await tenantAService.ListAsync(ct);
        var tenantBList = await tenantBService.ListAsync(ct);

        tenantAList.Select(r => r.RuntimeId).ShouldBe(new[] { "claude" });
        tenantBList.Select(r => r.RuntimeId).ShouldBe(new[] { "openai" });
    }

    private static (SpringDbContext Context, IAgentRuntimeRegistry Registry) CreateContextAndRegistry(string tenantId)
    {
        var context = CreateDbContext(Guid.NewGuid().ToString(), tenantId);
        return (context, CreateRegistry());
    }

    private static SpringDbContext CreateDbContext(string dbName, string tenantId)
    {
        var options = new DbContextOptionsBuilder<SpringDbContext>()
            .UseInMemoryDatabase(databaseName: dbName)
            .Options;
        return new SpringDbContext(options, new StaticTenantContext(tenantId));
    }

    private static IAgentRuntimeRegistry CreateRegistry()
    {
        var claude = CreateRuntime(
            id: "claude",
            displayName: "Claude",
            toolKind: "claude-code-cli",
            models: new[]
            {
                new ModelDescriptor("claude-sonnet-4-5", "Claude Sonnet 4.5", 200_000),
                new ModelDescriptor("claude-opus-4-1", "Claude Opus 4.1", 200_000),
            });
        var openai = CreateRuntime(
            id: "openai",
            displayName: "OpenAI",
            toolKind: "dapr-agent",
            models: new[]
            {
                new ModelDescriptor("gpt-4o", "GPT-4o", 128_000),
            });

        var registry = Substitute.For<IAgentRuntimeRegistry>();
        registry.All.Returns(new[] { claude, openai });
        registry.Get("claude").Returns(claude);
        registry.Get("openai").Returns(openai);
        registry.Get("unknown").Returns((IAgentRuntime?)null);
        return registry;
    }

    private static IAgentRuntime CreateRuntime(
        string id, string displayName, string toolKind, IReadOnlyList<ModelDescriptor> models)
    {
        var runtime = Substitute.For<IAgentRuntime>();
        runtime.Id.Returns(id);
        runtime.DisplayName.Returns(displayName);
        runtime.ToolKind.Returns(toolKind);
        runtime.DefaultModels.Returns(models);
        return runtime;
    }

    private static TenantAgentRuntimeInstallService CreateSut(
        SpringDbContext context, string tenantId, IAgentRuntimeRegistry registry)
    {
        var tenantContext = new StaticTenantContext(tenantId);
        return new TenantAgentRuntimeInstallService(
            context,
            tenantContext,
            registry,
            NullLogger<TenantAgentRuntimeInstallService>.Instance);
    }
}