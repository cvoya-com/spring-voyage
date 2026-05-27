// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connector.Slack.Tests;

using Cvoya.Spring.Connector.Slack.Outbound;
using Cvoya.Spring.Core.Tenancy;
using Cvoya.Spring.Dapr.Connectors.Slack;
using Cvoya.Spring.Dapr.Data;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using NSubstitute;

using Shouldly;

using Xunit;

/// <summary>
/// Integration coverage for <see cref="EfSlackThreadMapStore"/>
/// against an in-memory <see cref="SpringDbContext"/>. Pins the
/// insert / outbound-lookup / inbound-lookup / list contracts the
/// outbound dispatcher and inbound event handler rely on.
/// </summary>
public class SlackThreadMapStoreTests
{
    private static readonly Guid TestTenantId = new("aaaaaaaa-bbbb-cccc-dddd-000000000001");
    private static readonly Guid TestBoundUserId = new("11111111-0000-0000-0000-000000000000");

    [Fact]
    public async Task RecordAsync_PersistsRow_AndLookupOutboundReturnsIt()
    {
        var ct = TestContext.Current.CancellationToken;
        var harness = TestHarness.Create();

        var svThreadId = Guid.NewGuid();
        await harness.Store.RecordAsync(
            svThreadId,
            TestBoundUserId,
            "T-acme",
            "D1234567",
            "1700000000.123456",
            ct);

        var mapping = await harness.Store.LookupOutboundAsync(svThreadId, TestBoundUserId, "T-acme", ct);
        mapping.ShouldNotBeNull();
        mapping!.SvThreadId.ShouldBe(svThreadId);
        mapping.SlackChannelId.ShouldBe("D1234567");
        mapping.SlackThreadTs.ShouldBe("1700000000.123456");
        mapping.TeamId.ShouldBe("T-acme");
    }

    [Fact]
    public async Task LookupOutboundAsync_MissingRow_ReturnsNull()
    {
        var ct = TestContext.Current.CancellationToken;
        var harness = TestHarness.Create();

        var mapping = await harness.Store.LookupOutboundAsync(Guid.NewGuid(), TestBoundUserId, "T-acme", ct);
        mapping.ShouldBeNull();
    }

    [Fact]
    public async Task LookupSvThreadAsync_RoundTripsByThreadTs()
    {
        var ct = TestContext.Current.CancellationToken;
        var harness = TestHarness.Create();

        var svThreadId = Guid.NewGuid();
        await harness.Store.RecordAsync(
            svThreadId,
            TestBoundUserId,
            "T-acme",
            "D1234567",
            "1700000000.999999",
            ct);

        var mapping = await harness.Store.LookupSvThreadAsync("T-acme", "1700000000.999999", ct);
        mapping.ShouldNotBeNull();
        mapping!.SvThreadId.ShouldBe(svThreadId);
    }

    [Fact]
    public async Task ListForBoundUserAsync_ReturnsAllMappingsForUser()
    {
        var ct = TestContext.Current.CancellationToken;
        var harness = TestHarness.Create();

        var thread1 = Guid.NewGuid();
        var thread2 = Guid.NewGuid();
        await harness.Store.RecordAsync(thread1, TestBoundUserId, "T-acme", "D1", "1700.111", ct);
        await harness.Store.RecordAsync(thread2, TestBoundUserId, "T-acme", "D1", "1700.222", ct);

        var list = await harness.Store.ListForBoundUserAsync(TestBoundUserId, "T-acme", ct);
        list.Count.ShouldBe(2);
        list.Select(m => m.SvThreadId).ShouldContain(thread1);
        list.Select(m => m.SvThreadId).ShouldContain(thread2);
    }

    [Fact]
    public async Task RecordAsync_RepeatedInsert_UpdatesInPlace()
    {
        var ct = TestContext.Current.CancellationToken;
        var harness = TestHarness.Create();

        var svThreadId = Guid.NewGuid();
        await harness.Store.RecordAsync(svThreadId, TestBoundUserId, "T-acme", "D1", "1700.111", ct);
        await harness.Store.RecordAsync(svThreadId, TestBoundUserId, "T-acme", "D2", "1700.222", ct);

        // The outbound key is unique on (tenant, sv_thread, bound_user, team)
        // — repeated calls update the existing row rather than insert.
        var mapping = await harness.Store.LookupOutboundAsync(svThreadId, TestBoundUserId, "T-acme", ct);
        mapping.ShouldNotBeNull();
        mapping!.SlackChannelId.ShouldBe("D2");
        mapping.SlackThreadTs.ShouldBe("1700.222");
    }

    private sealed class TestHarness
    {
        public EfSlackThreadMapStore Store { get; }

        private TestHarness(EfSlackThreadMapStore store)
        {
            Store = store;
        }

        public static TestHarness Create()
        {
            var services = new ServiceCollection();
            services.AddLogging();

            var tenantContext = Substitute.For<ITenantContext>();
            tenantContext.CurrentTenantId.Returns(TestTenantId);
            services.AddSingleton(tenantContext);

            var dbName = $"SlackThreadMapStoreTests_{Guid.NewGuid():N}";
            services.AddDbContext<SpringDbContext>(o => o.UseInMemoryDatabase(dbName));

            // Provide an ITenantContext for SpringDbContext's 2-arg ctor.

            var provider = services.BuildServiceProvider();
            var store = new EfSlackThreadMapStore(provider.GetRequiredService<IServiceScopeFactory>());
            return new TestHarness(store);
        }
    }
}
