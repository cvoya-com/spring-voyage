// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Tests.Connectors;

using System.Text.Json;

using Cvoya.Spring.Connectors;
using Cvoya.Spring.Dapr.Connectors;
using Cvoya.Spring.Dapr.Data;
using Cvoya.Spring.Dapr.Tenancy;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

using NSubstitute;

using Shouldly;

using Xunit;

/// <summary>
/// Unit tests for <see cref="TenantConnectorInstallService"/>. Parallel
/// coverage to the agent-runtime install service.
/// </summary>
public class TenantConnectorInstallServiceTests
{
    private const string TenantA = "tenant-a";
    private const string TenantB = "tenant-b";

    [Fact]
    public async Task InstallAsync_NoConfig_PersistsEmpty()
    {
        var ct = TestContext.Current.CancellationToken;
        var context = CreateDbContext(Guid.NewGuid().ToString(), TenantA);
        var sut = CreateSut(context, TenantA);

        var result = await sut.InstallAsync("github", config: null, ct);

        result.ConnectorId.ShouldBe("github");
        result.TenantId.ShouldBe(TenantA);
        result.Config.Config.ShouldBeNull();
    }

    [Fact]
    public async Task InstallAsync_WithConfig_PersistsProvidedConfig()
    {
        var ct = TestContext.Current.CancellationToken;
        var context = CreateDbContext(Guid.NewGuid().ToString(), TenantA);
        var sut = CreateSut(context, TenantA);

        var payload = JsonSerializer.SerializeToElement(new { org = "cvoya-com" });
        var config = new ConnectorInstallConfig(payload);

        var result = await sut.InstallAsync("github", config, ct);

        result.Config.Config.ShouldNotBeNull();
        result.Config.Config!.Value.GetProperty("org").GetString().ShouldBe("cvoya-com");
    }

    [Fact]
    public async Task InstallAsync_Repeat_PreservesConfigWhenNull()
    {
        var ct = TestContext.Current.CancellationToken;
        var context = CreateDbContext(Guid.NewGuid().ToString(), TenantA);
        var sut = CreateSut(context, TenantA);

        var payload = JsonSerializer.SerializeToElement(new { org = "cvoya-com" });
        await sut.InstallAsync("github", new ConnectorInstallConfig(payload), ct);

        var refreshed = await sut.InstallAsync("github", config: null, ct);

        refreshed.Config.Config.ShouldNotBeNull();
        refreshed.Config.Config!.Value.GetProperty("org").GetString().ShouldBe("cvoya-com");
    }

    [Fact]
    public async Task InstallAsync_AcceptsTypeIdOrSlug_CanonicalisesToSlug()
    {
        var ct = TestContext.Current.CancellationToken;
        var context = CreateDbContext(Guid.NewGuid().ToString(), TenantA);
        var sut = CreateSut(context, TenantA);

        // Install by GUID id, then look up by slug — must hit the same row.
        var typeId = StubConnectorTypeId;
        await sut.InstallAsync(typeId.ToString(), config: null, ct);

        var viaSlug = await sut.GetAsync("github", ct);
        viaSlug.ShouldNotBeNull();
        viaSlug!.ConnectorId.ShouldBe("github");
    }

    [Fact]
    public async Task UninstallAsync_SoftDeletesRow_HiddenFromList()
    {
        var ct = TestContext.Current.CancellationToken;
        var context = CreateDbContext(Guid.NewGuid().ToString(), TenantA);
        var sut = CreateSut(context, TenantA);

        await sut.InstallAsync("github", config: null, ct);
        await sut.UninstallAsync("github", ct);

        var list = await sut.ListAsync(ct);
        list.ShouldBeEmpty();
    }

    [Fact]
    public async Task UpdateConfigAsync_NotInstalled_Throws()
    {
        var ct = TestContext.Current.CancellationToken;
        var context = CreateDbContext(Guid.NewGuid().ToString(), TenantA);
        var sut = CreateSut(context, TenantA);

        await Should.ThrowAsync<InvalidOperationException>(
            async () => await sut.UpdateConfigAsync(
                "github",
                ConnectorInstallConfig.Empty,
                ct));
    }

    [Fact]
    public async Task ListAsync_HonoursTenantIsolation()
    {
        var ct = TestContext.Current.CancellationToken;
        var dbName = Guid.NewGuid().ToString();

        var sutA = CreateSut(CreateDbContext(dbName, TenantA), TenantA);
        var sutB = CreateSut(CreateDbContext(dbName, TenantB), TenantB);

        await sutA.InstallAsync("github", config: null, ct);
        await sutB.InstallAsync("arxiv", config: null, ct);

        (await sutA.ListAsync(ct)).Select(r => r.ConnectorId).ShouldBe(new[] { "github" });
        (await sutB.ListAsync(ct)).Select(r => r.ConnectorId).ShouldBe(new[] { "arxiv" });
    }

    private static readonly Guid StubConnectorTypeId = new("00000000-0000-0000-0000-00000000beef");

    private static SpringDbContext CreateDbContext(string dbName, string tenantId)
    {
        var options = new DbContextOptionsBuilder<SpringDbContext>()
            .UseInMemoryDatabase(databaseName: dbName)
            .Options;
        return new SpringDbContext(options, new StaticTenantContext(tenantId));
    }

    private static IConnectorType[] CreateConnectorStubs()
    {
        var github = Substitute.For<IConnectorType>();
        github.TypeId.Returns(StubConnectorTypeId);
        github.Slug.Returns("github");
        github.DisplayName.Returns("GitHub");
        github.Description.Returns("GitHub connector (test stub)");

        var arxiv = Substitute.For<IConnectorType>();
        arxiv.TypeId.Returns(new Guid("00000000-0000-0000-0000-00000000a71a"));
        arxiv.Slug.Returns("arxiv");
        arxiv.DisplayName.Returns("arXiv");
        arxiv.Description.Returns("arXiv connector (test stub)");

        return new[] { github, arxiv };
    }

    private static TenantConnectorInstallService CreateSut(
        SpringDbContext context, string tenantId)
    {
        return new TenantConnectorInstallService(
            context,
            new StaticTenantContext(tenantId),
            CreateConnectorStubs(),
            NullLogger<TenantConnectorInstallService>.Instance);
    }
}