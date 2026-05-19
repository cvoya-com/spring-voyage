// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Tests.Observability;

using Cvoya.Spring.Core.Capabilities;
using Cvoya.Spring.Core.Tenancy;
using Cvoya.Spring.Dapr.Data;
using Cvoya.Spring.Dapr.Observability;
using Cvoya.Spring.Dapr.Tenancy;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;

using Shouldly;

using Xunit;

/// <summary>
/// Issue #2492 — round-trip tests for the tenant activity-settings store.
/// </summary>
public class TenantActivitySettingsServiceTests : IDisposable
{
    private static readonly Guid TenantA = new("eeeeeeee-0000-0000-0000-000000000001");

    private readonly ServiceProvider _serviceProvider;

    public TenantActivitySettingsServiceTests()
    {
        // Per-test-class InMemoryDatabaseRoot ensures every DbContext built
        // by the DI container shares the same in-memory store. Without the
        // explicit root, the named registry can rebuild per-scope under
        // certain test sequences.
        var dbName = Guid.NewGuid().ToString();
        var root = new InMemoryDatabaseRoot();

        var services = new ServiceCollection();
        services.AddSingleton<ITenantContext>(new StaticTenantContext(TenantA));
        services.AddDbContext<SpringDbContext>(o => o.UseInMemoryDatabase(dbName, root));
        services.AddSingleton(TimeProvider.System);
        services.AddScoped<ITenantActivitySettings, TenantActivitySettingsService>();
        _serviceProvider = services.BuildServiceProvider();

        using var setupScope = _serviceProvider.CreateScope();
        var db = setupScope.ServiceProvider.GetRequiredService<SpringDbContext>();
        db.Database.EnsureCreated();
    }

    public void Dispose() => _serviceProvider.Dispose();

    [Fact]
    public async Task GetAsync_MissingRow_ReturnsDefaults()
    {
        using var scope = _serviceProvider.CreateScope();
        var settings = scope.ServiceProvider.GetRequiredService<ITenantActivitySettings>();

        var snapshot = await settings.GetAsync(TenantA, TestContext.Current.CancellationToken);

        snapshot.Level.ShouldBe(ITenantActivitySettings.DefaultLevel);
        snapshot.RetentionDays.ShouldBe(ITenantActivitySettings.DefaultRetentionDays);
    }

    [Fact]
    public async Task DirectDbAdd_PersistsAcrossScopes()
    {
        // Diagnostic test: bypass the service and write directly to
        // verify the in-memory DB shares state across scopes.
        using (var scope1 = _serviceProvider.CreateScope())
        {
            var db = scope1.ServiceProvider.GetRequiredService<SpringDbContext>();
            db.TenantActivitySettings.Add(new Cvoya.Spring.Dapr.Data.Entities.TenantActivitySettingsEntity
            {
                TenantId = TenantA,
                Level = "Off",
                RetentionDays = 3,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        using var scope2 = _serviceProvider.CreateScope();
        var db2 = scope2.ServiceProvider.GetRequiredService<SpringDbContext>();
        var found = await db2.TenantActivitySettings
            .Where(s => s.TenantId == TenantA)
            .FirstOrDefaultAsync(TestContext.Current.CancellationToken);
        found.ShouldNotBeNull();
        found!.Level.ShouldBe("Off");
    }

    [Fact]
    public async Task SetAsync_ThenGetAsync_RoundTripsLevelAndRetention()
    {
        using (var scope1 = _serviceProvider.CreateScope())
        {
            var settings = scope1.ServiceProvider.GetRequiredService<ITenantActivitySettings>();
            await settings.SetAsync(TenantA, ActivityCaptureLevel.Summary, 7, TestContext.Current.CancellationToken);
        }

        using var scope2 = _serviceProvider.CreateScope();
        var settings2 = scope2.ServiceProvider.GetRequiredService<ITenantActivitySettings>();
        var snapshot = await settings2.GetAsync(TenantA, TestContext.Current.CancellationToken);

        snapshot.Level.ShouldBe(ActivityCaptureLevel.Summary);
        snapshot.RetentionDays.ShouldBe(7);
    }

    [Fact]
    public async Task SetAsync_PartialUpdate_LeavesOtherFieldUnchanged()
    {
        using var scope = _serviceProvider.CreateScope();
        var settings = scope.ServiceProvider.GetRequiredService<ITenantActivitySettings>();

        await settings.SetAsync(TenantA, ActivityCaptureLevel.Full, 30, TestContext.Current.CancellationToken);
        await settings.SetAsync(TenantA, level: null, retentionDays: 14, TestContext.Current.CancellationToken);
        var snapshot = await settings.GetAsync(TenantA, TestContext.Current.CancellationToken);

        snapshot.Level.ShouldBe(ActivityCaptureLevel.Full);
        snapshot.RetentionDays.ShouldBe(14);
    }
}
