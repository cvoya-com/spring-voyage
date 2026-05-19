// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Tests.Connectors;

using System.Text.Json;

using Cvoya.Spring.Core.Tenancy;
using Cvoya.Spring.Dapr.Connectors;
using Cvoya.Spring.Dapr.Data;
using Cvoya.Spring.Dapr.Data.Entities;
using Cvoya.Spring.Dapr.Tenancy;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

using Shouldly;

using Xunit;

/// <summary>
/// Coverage for ADR-0047 §10's cross-tenant rejection probe. The
/// probe walks every other tenant's bindings of the supplied connector
/// type, extracts each binding's <c>repo</c> fingerprint from its
/// typed-config JSON, and reports whether the supplied fingerprint
/// collides. The current tenant's own bindings are deliberately
/// excluded — in-tenant fan-out is the supported configuration.
/// </summary>
public class UnitConnectorBindingCrossTenantProbeTests : IDisposable
{
    private static readonly Guid GitHubTypeId = new("11111111-1111-1111-1111-111111111111");
    private static readonly Guid CurrentTenant = new("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid OtherTenant = new("bbbbbbbb-0000-0000-0000-000000000002");
    private static readonly Guid AnotherTenant = new("cccccccc-0000-0000-0000-000000000003");

    private readonly string _dbName = Guid.NewGuid().ToString();
    private readonly ServiceProvider _serviceProvider;

    public UnitConnectorBindingCrossTenantProbeTests()
    {
        _serviceProvider = BuildServiceProvider();
    }

    private ServiceProvider BuildServiceProvider()
    {
        var services = new ServiceCollection();
        services.AddSingleton<ITenantContext>(new StaticTenantContext(CurrentTenant));
        services.AddDbContext<SpringDbContext>(o => o.UseInMemoryDatabase(_dbName));
        return services.BuildServiceProvider();
    }

    [Fact]
    public async Task HasCrossTenantBindingAsync_NoBindingsAnywhere_ReturnsFalse()
    {
        var ct = TestContext.Current.CancellationToken;
        await EnsureCreatedAsync();

        var probe = CreateProbe();

        var hit = await probe.HasCrossTenantBindingAsync(
            GitHubTypeId, "acme/platform", ct);

        hit.ShouldBeFalse();
    }

    [Fact]
    public async Task HasCrossTenantBindingAsync_SameTenantBinding_ReturnsFalse()
    {
        // In-tenant fan-out is supported — a binding for the same repo
        // within the SAME tenant does NOT trip the cross-tenant probe.
        var ct = TestContext.Current.CancellationToken;
        await SeedBindingAsync(CurrentTenant, "acme/platform");

        var probe = CreateProbe();

        var hit = await probe.HasCrossTenantBindingAsync(
            GitHubTypeId, "acme/platform", ct);

        hit.ShouldBeFalse();
    }

    [Fact]
    public async Task HasCrossTenantBindingAsync_OtherTenantSameRepo_ReturnsTrue()
    {
        // The collision case ADR-0047 §10 rejects at binding-create time.
        var ct = TestContext.Current.CancellationToken;
        await SeedBindingAsync(OtherTenant, "acme/platform");

        var probe = CreateProbe();

        var hit = await probe.HasCrossTenantBindingAsync(
            GitHubTypeId, "acme/platform", ct);

        hit.ShouldBeTrue();
    }

    [Fact]
    public async Task HasCrossTenantBindingAsync_OwnerRepoCaseInsensitiveMatch_ReturnsTrue()
    {
        // GitHub login comparisons are case-insensitive; the probe
        // mirrors the matcher's case-insensitive semantics so cross-tenant
        // claims cannot evade detection by casing differences.
        var ct = TestContext.Current.CancellationToken;
        await SeedBindingAsync(OtherTenant, "Acme/Platform");

        var probe = CreateProbe();

        var hit = await probe.HasCrossTenantBindingAsync(
            GitHubTypeId, "acme/platform", ct);

        hit.ShouldBeTrue();
    }

    [Fact]
    public async Task HasCrossTenantBindingAsync_OtherTenantDifferentRepo_ReturnsFalse()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedBindingAsync(OtherTenant, "acme/other-repo");

        var probe = CreateProbe();

        var hit = await probe.HasCrossTenantBindingAsync(
            GitHubTypeId, "acme/platform", ct);

        hit.ShouldBeFalse();
    }

    [Fact]
    public async Task HasCrossTenantBindingAsync_MultipleOtherTenants_ReturnsTrueOnAnyMatch()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedBindingAsync(OtherTenant, "acme/other-repo");
        await SeedBindingAsync(AnotherTenant, "acme/platform");

        var probe = CreateProbe();

        var hit = await probe.HasCrossTenantBindingAsync(
            GitHubTypeId, "acme/platform", ct);

        hit.ShouldBeTrue();
    }

    private UnitConnectorBindingCrossTenantProbe CreateProbe()
    {
        return new UnitConnectorBindingCrossTenantProbe(
            _serviceProvider.GetRequiredService<IServiceScopeFactory>(),
            _serviceProvider.GetRequiredService<ITenantContext>(),
            NullLogger<UnitConnectorBindingCrossTenantProbe>.Instance);
    }

    private async Task EnsureCreatedAsync()
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SpringDbContext>();
        await db.Database.EnsureCreatedAsync();
    }

    private async Task SeedBindingAsync(Guid tenantId, string repo)
    {
        await EnsureCreatedAsync();
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SpringDbContext>();

        // Bypass the SpringDbContext tenant query filter and the
        // ITenantContext-driven insert stamp by writing the row directly.
        // The probe queries with IgnoreQueryFilters so it sees rows
        // regardless of the ambient tenant.
        db.UnitConnectorBindings.Add(new UnitConnectorBindingEntity
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            UnitId = Guid.NewGuid(),
            ConnectorType = GitHubTypeId,
            Config = JsonSerializer.SerializeToElement(new
            {
                repo,
                appInstallationId = 1L,
            }),
            Metadata = null,
            BoundAt = DateTimeOffset.UtcNow,
        });

        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    public void Dispose()
    {
        _serviceProvider.Dispose();
        GC.SuppressFinalize(this);
    }
}
