// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Tests.Observability;

using Cvoya.Spring.Core.Capabilities;
using Cvoya.Spring.Core.Tenancy;
using Cvoya.Spring.Dapr.Data;
using Cvoya.Spring.Dapr.Data.Entities;
using Cvoya.Spring.Dapr.Observability;
using Cvoya.Spring.Dapr.Tenancy;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

using NSubstitute;

using Shouldly;

using Xunit;

/// <summary>
/// Issue #2492 — verifies the retention sweep deletes <c>activity_events</c>
/// rows older than each tenant's retention horizon, leaving in-window
/// rows intact.
/// </summary>
public class ActivityRetentionPurgeServiceTests : IDisposable
{
    private static readonly Guid TenantA = new("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid TenantB = new("bbbbbbbb-0000-0000-0000-000000000001");

    private readonly ServiceProvider _serviceProvider;

    public ActivityRetentionPurgeServiceTests()
    {
        var dbName = Guid.NewGuid().ToString();
        var root = new InMemoryDatabaseRoot();

        var services = new ServiceCollection();
        services.AddSingleton<ITenantContext>(new StaticTenantContext(TenantA));
        // Bypass is required by the purge sweep; the OSS implementation just logs.
        services.AddSingleton<ITenantScopeBypass>(NoOpTenantScopeBypass.Instance);
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
    public async Task PurgeOnceAsync_DeletesEventsOlderThanTenantHorizon()
    {
        // Tenant A keeps 7 days, tenant B keeps default (30).
        using (var scope = _serviceProvider.CreateScope())
        {
            var settings = scope.ServiceProvider.GetRequiredService<ITenantActivitySettings>();
            await settings.SetAsync(TenantA, ActivityCaptureLevel.Full, 7, TestContext.Current.CancellationToken);
        }

        var now = DateTimeOffset.UtcNow;
        using (var scope = _serviceProvider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<SpringDbContext>();
            db.ActivityEvents.AddRange(
                NewEvent(TenantA, now.AddDays(-10)),  // tenant A, expired (older than 7d)
                NewEvent(TenantA, now.AddDays(-1)),   // tenant A, in-window
                NewEvent(TenantB, now.AddDays(-35)),  // tenant B, expired (older than 30d)
                NewEvent(TenantB, now.AddDays(-5)));  // tenant B, in-window
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var purger = new ActivityRetentionPurgeService(
            _serviceProvider.GetRequiredService<IServiceScopeFactory>(),
            TimeProvider.System,
            NullLogger<ActivityRetentionPurgeService>.Instance);
        await purger.PurgeOnceAsync(TestContext.Current.CancellationToken);

        using var verifyScope = _serviceProvider.CreateScope();
        var dbReadback = verifyScope.ServiceProvider.GetRequiredService<SpringDbContext>();
        var remaining = await dbReadback.ActivityEvents.IgnoreQueryFilters()
            .ToListAsync(TestContext.Current.CancellationToken);

        remaining.Count.ShouldBe(2);
        remaining.ShouldContain(r => r.TenantId == TenantA && r.Timestamp > now.AddDays(-7));
        remaining.ShouldContain(r => r.TenantId == TenantB && r.Timestamp > now.AddDays(-30));
    }

    private static ActivityEventRecord NewEvent(Guid tenantId, DateTimeOffset timestamp)
        => new()
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            SourceId = Guid.NewGuid(),
            EventType = ActivityEventType.RuntimeLog.ToString(),
            Severity = ActivitySeverity.Info.ToString(),
            Summary = "retention test",
            Timestamp = timestamp,
        };

    /// <summary>
    /// Minimal <see cref="ITenantScopeBypass"/> that yields a disposable
    /// no-op scope. Sufficient for purge-sweep tests; the OSS implementation
    /// adds structured logging that isn't load-bearing for the deletion behaviour.
    /// </summary>
    private sealed class NoOpTenantScopeBypass : ITenantScopeBypass
    {
        public static readonly NoOpTenantScopeBypass Instance = new();
        public bool IsBypassActive => true;
        public IDisposable BeginBypass(string reason) => NullDisposable.Instance;
        private sealed class NullDisposable : IDisposable
        {
            public static readonly NullDisposable Instance = new();
            public void Dispose() { }
        }
    }
}
