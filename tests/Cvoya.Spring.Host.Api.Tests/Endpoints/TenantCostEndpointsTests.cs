// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Api.Tests.Endpoints;

using System.Net;
using System.Net.Http.Json;

using Cvoya.Spring.Core.Costs;
using Cvoya.Spring.Dapr.Costs;
using Cvoya.Spring.Dapr.Data;
using Cvoya.Spring.Host.Api.Models;

using Microsoft.Extensions.DependencyInjection;

using Shouldly;

using Xunit;

/// <summary>
/// Integration tests for <c>GET /api/v1/tenant/cost/timeseries</c>
/// (V21-tenant-cost-timeseries, #916). Covers shape, zero-filling,
/// sum-matches-total invariant, cache header, and parameter validation.
/// </summary>
public class TenantCostEndpointsTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public TenantCostEndpointsTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task GetTimeseries_WithRecords_ReturnsZeroFilledBucketsMatchingTotal()
    {
        var ct = TestContext.Current.CancellationToken;

        // Seed cost rows across several distinct days inside the default
        // 30d window. Two rows go into one bucket so we also exercise
        // in-bucket summation. Because the shared in-memory fixture is
        // re-used across tests, other tests may have seeded additional
        // "default" tenant rows inside the 30d window — we compare the
        // timeseries sum against the canonical GetTenantCostAsync total
        // rather than a hardcoded number so the assertion stays robust.
        var now = DateTimeOffset.UtcNow;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<SpringDbContext>();
            db.CostRecords.AddRange(
                CreateRecord(tenantId: "default", cost: 0.10m, timestamp: now.AddDays(-2)),
                CreateRecord(tenantId: "default", cost: 0.25m, timestamp: now.AddDays(-2).AddHours(3)),
                CreateRecord(tenantId: "default", cost: 0.50m, timestamp: now.AddDays(-10)),
                // Out-of-window row — must not appear in any bucket.
                CreateRecord(tenantId: "default", cost: 99m, timestamp: now.AddDays(-60)));
            await db.SaveChangesAsync(ct);
        }

        var response = await _client.GetAsync("/api/v1/tenant/cost/timeseries", ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var body = await response.Content.ReadFromJsonAsync<CostTimeseriesResponse>(ct);
        body.ShouldNotBeNull();
        body!.Bucket.ShouldBe("1d");
        body.Series.Count.ShouldBe(30);

        // Every bucket is emitted — empty buckets carry 0 (zero-fill).
        body.Series.ShouldAllBe(b => b.Cost >= 0m);

        // Buckets are monotonically advancing by the bucket span.
        for (var i = 1; i < body.Series.Count; i++)
        {
            (body.Series[i].T - body.Series[i - 1].T).ShouldBe(TimeSpan.FromDays(1));
        }

        // Sum across every bucket matches the canonical tenant total for
        // the same window. This is the key invariant — operators trust the
        // header tile and the chart to agree.
        using (var scope = _factory.Services.CreateScope())
        {
            var costService = scope.ServiceProvider.GetRequiredService<ICostQueryService>();
            var canonical = await costService.GetTenantCostAsync(
                "default", body.From, body.To, ct);

            body.Series.Sum(b => b.Cost).ShouldBe(canonical.TotalCost);

            // The 0.85 we just added must be visible in the canonical
            // total — guards against the endpoint quietly filtering the
            // rows out on a wrong tenant/window path.
            canonical.TotalCost.ShouldBeGreaterThanOrEqualTo(0.85m);
        }
    }

    [Fact]
    public async Task GetTimeseries_HonorsWindowAndBucketParams()
    {
        var ct = TestContext.Current.CancellationToken;

        var response = await _client.GetAsync(
            "/api/v1/tenant/cost/timeseries?window=7d&bucket=1d", ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var body = await response.Content.ReadFromJsonAsync<CostTimeseriesResponse>(ct);
        body.ShouldNotBeNull();
        body!.Bucket.ShouldBe("1d");
        body.Series.Count.ShouldBe(7);

        // First bucket anchored on `from` — sanity check the ordering.
        body.Series[0].T.ShouldBe(body.From);
    }

    [Fact]
    public async Task GetTimeseries_HourlyBucketOnOneDayWindow_Returns24Buckets()
    {
        var ct = TestContext.Current.CancellationToken;

        var response = await _client.GetAsync(
            "/api/v1/tenant/cost/timeseries?window=1d&bucket=1h", ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var body = await response.Content.ReadFromJsonAsync<CostTimeseriesResponse>(ct);
        body.ShouldNotBeNull();
        body!.Bucket.ShouldBe("1h");
        body.Series.Count.ShouldBe(24);
    }

    [Fact]
    public async Task GetTimeseries_SetsCacheControlPrivate60s()
    {
        var ct = TestContext.Current.CancellationToken;

        var response = await _client.GetAsync("/api/v1/tenant/cost/timeseries", ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        response.Headers.CacheControl.ShouldNotBeNull();
        response.Headers.CacheControl!.Private.ShouldBeTrue();
        response.Headers.CacheControl.MaxAge.ShouldBe(TimeSpan.FromSeconds(60));
    }

    [Fact]
    public async Task GetTimeseries_RejectsUnparseableWindow()
    {
        var ct = TestContext.Current.CancellationToken;

        var response = await _client.GetAsync(
            "/api/v1/tenant/cost/timeseries?window=notaduration", ct);
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task GetTimeseries_RejectsUnknownBucket()
    {
        var ct = TestContext.Current.CancellationToken;

        var response = await _client.GetAsync(
            "/api/v1/tenant/cost/timeseries?bucket=3d", ct);
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task GetTimeseries_RejectsWindowBeyond90Days()
    {
        var ct = TestContext.Current.CancellationToken;

        var response = await _client.GetAsync(
            "/api/v1/tenant/cost/timeseries?window=120d", ct);
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task GetTimeseries_RejectsBucketLargerThanWindow()
    {
        var ct = TestContext.Current.CancellationToken;

        var response = await _client.GetAsync(
            "/api/v1/tenant/cost/timeseries?window=1d&bucket=7d", ct);
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    private static CostRecord CreateRecord(
        string tenantId = "default",
        string agentId = "agent-ts",
        string? unitId = "unit-ts",
        decimal cost = 0.05m,
        int inputTokens = 100,
        int outputTokens = 50,
        DateTimeOffset? timestamp = null,
        CostSource source = CostSource.Work)
    {
        return new CostRecord
        {
            Id = Guid.NewGuid(),
            AgentId = agentId,
            UnitId = unitId,
            TenantId = tenantId,
            Model = "claude-3-opus",
            Cost = cost,
            InputTokens = inputTokens,
            OutputTokens = outputTokens,
            Timestamp = timestamp ?? DateTimeOffset.UtcNow,
            Source = source,
        };
    }
}