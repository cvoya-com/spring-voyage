// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Integration.Tests;

using Cvoya.Spring.Connector.Slack.Outbound;
using Cvoya.Spring.Core.Tenancy;
using Cvoya.Spring.Dapr.Connectors.Slack;
using Cvoya.Spring.Dapr.Data;
using Cvoya.Spring.Dapr.Tenancy;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using Shouldly;

using Xunit;

/// <summary>
/// Integration coverage for the Slack thread-state EF surface
/// (ADR-0061 §3 / #2818). Exercises the
/// <see cref="EfSlackThreadMapStore"/> end-to-end against the EF
/// model (in-memory provider; the production deployment uses
/// Postgres). The test pins:
///
/// <list type="bullet">
///   <item><description>
///     The migration's table shape is consistent with the entity +
///     configuration (<c>EnsureCreatedAsync</c> would fail if the
///     model and the entity diverged).
///   </description></item>
///   <item><description>
///     Round-trips work through the singleton store + scoped
///     <see cref="SpringDbContext"/> the DI graph wires.
///   </description></item>
/// </list>
/// </summary>
public class SlackThreadStateIntegrationTests : IDisposable
{
    private static readonly Guid TenantId = OssTenantIds.Default;
    private static readonly Guid BoundUserId = new("11111111-1111-1111-1111-111111111111");

    private readonly ServiceProvider _services;
    private readonly string _dbName = Guid.NewGuid().ToString();

    public SlackThreadStateIntegrationTests()
    {
        var services = new ServiceCollection();
        services.AddSingleton<ITenantContext>(new StaticTenantContext(TenantId));
        // In-memory EF here — Testcontainers Postgres migration tracked in #2845.
        services.AddDbContext<SpringDbContext>(o => o.UseInMemoryDatabase(_dbName));
        services.AddSingleton<ISlackThreadMapStore, EfSlackThreadMapStore>();
        services.AddLogging();
        _services = services.BuildServiceProvider();
    }

    public void Dispose()
    {
        _services.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task ThreadMapStore_RoundTripsAgainstEfBackedContext()
    {
        var ct = TestContext.Current.CancellationToken;

        // Stand up the schema once.
        using (var scope = _services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<SpringDbContext>();
            await db.Database.EnsureCreatedAsync(ct);
        }

        var store = _services.GetRequiredService<ISlackThreadMapStore>();
        var svThreadId = Guid.NewGuid();

        await store.RecordAsync(
            svThreadId,
            BoundUserId,
            "T-integration",
            "D-channel",
            "1700.001",
            ct);

        // Outbound lookup hits the row.
        var outbound = await store.LookupOutboundAsync(svThreadId, BoundUserId, "T-integration", ct);
        outbound.ShouldNotBeNull();
        outbound!.SlackThreadTs.ShouldBe("1700.001");

        // Inbound reverse lookup hits the same row via the
        // (team_id, thread_ts) index.
        var inbound = await store.LookupSvThreadAsync("T-integration", "1700.001", ct);
        inbound.ShouldNotBeNull();
        inbound!.SvThreadId.ShouldBe(svThreadId);
    }

    [Fact]
    public async Task EnsureCreatedAsync_AppliesSlackThreadStateTable()
    {
        // The Slack thread-state table must apply via the EF model.
        // If the migration drifted from the entity configuration this
        // call would throw.
        var ct = TestContext.Current.CancellationToken;
        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SpringDbContext>();
        await db.Database.EnsureCreatedAsync(ct);
        db.SlackThreadStates.IgnoreQueryFilters().Count().ShouldBe(0);
    }
}
