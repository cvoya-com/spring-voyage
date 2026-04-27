// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Api.Tests;

using System.Net;
using System.Net.Http.Json;

using Cvoya.Spring.Core.Observability;
using Cvoya.Spring.Host.Api.Models;

using NSubstitute;

using Shouldly;

using Xunit;

/// <summary>
/// PR-C3 (#457): integration tests for the new
/// <c>/api/v1/analytics/throughput</c> and <c>/api/v1/analytics/waits</c>
/// endpoints. These drive the portal's Analytics surface (#448) and the
/// `spring analytics` CLI verbs.
/// </summary>
public class AnalyticsEndpointsTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public AnalyticsEndpointsTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task GetThroughput_ReturnsEntriesFromQueryService()
    {
        var ct = TestContext.Current.CancellationToken;

        var rollup = new ThroughputRollup(
            new List<ThroughputEntry>
            {
                new("agent://ada", 10, 8, 4, 3),
                new("agent://grace", 1, 1, 1, 0),
            },
            DateTimeOffset.Parse("2026-04-01T00:00:00Z"),
            DateTimeOffset.Parse("2026-04-16T00:00:00Z"));

        _factory.AnalyticsQueryService.GetThroughputAsync(
                Arg.Any<string?>(),
                Arg.Any<DateTimeOffset>(),
                Arg.Any<DateTimeOffset>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(rollup));

        var response = await _client.GetAsync("/api/v1/tenant/analytics/throughput", ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<ThroughputRollupResponse>(ct);
        body.ShouldNotBeNull();
        body!.Entries.Count.ShouldBe(2);
        body.Entries[0].Source.ShouldBe("agent://ada");
        body.Entries[0].MessagesReceived.ShouldBe(10);
    }

    [Fact]
    public async Task GetThroughput_ForwardsSourceFilter()
    {
        var ct = TestContext.Current.CancellationToken;

        _factory.AnalyticsQueryService.GetThroughputAsync(
                Arg.Any<string?>(),
                Arg.Any<DateTimeOffset>(),
                Arg.Any<DateTimeOffset>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new ThroughputRollup(
                new List<ThroughputEntry>(), DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow)));

        var response = await _client.GetAsync(
            "/api/v1/tenant/analytics/throughput?source=unit%3A%2F%2Feng-team", ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        await _factory.AnalyticsQueryService.Received(1).GetThroughputAsync(
            "unit://eng-team",
            Arg.Any<DateTimeOffset>(),
            Arg.Any<DateTimeOffset>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetWaits_ReturnsDurationsAndTransitionCountFromQueryService()
    {
        var ct = TestContext.Current.CancellationToken;

        // Durations are computed by AnalyticsQueryService from paired
        // StateChanged events (#476); the endpoint just projects whatever
        // the service returns. The direct computation is covered by
        // AnalyticsQueryServiceTests in the Dapr test project — here we
        // verify the endpoint surfaces non-zero duration values end-to-end.
        var rollup = new WaitTimeRollup(
            new List<WaitTimeEntry>
            {
                new("agent://ada", IdleSeconds: 120, BusySeconds: 45, WaitingForHumanSeconds: 30, StateTransitions: 9),
            },
            DateTimeOffset.Parse("2026-04-01T00:00:00Z"),
            DateTimeOffset.Parse("2026-04-16T00:00:00Z"));

        _factory.AnalyticsQueryService.GetWaitTimesAsync(
                Arg.Any<string?>(),
                Arg.Any<DateTimeOffset>(),
                Arg.Any<DateTimeOffset>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(rollup));

        var response = await _client.GetAsync("/api/v1/tenant/analytics/waits", ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<WaitTimeRollupResponse>(ct);
        body.ShouldNotBeNull();
        body!.Entries.Count.ShouldBe(1);
        body.Entries[0].IdleSeconds.ShouldBe(120);
        body.Entries[0].BusySeconds.ShouldBe(45);
        body.Entries[0].WaitingForHumanSeconds.ShouldBe(30);
        body.Entries[0].StateTransitions.ShouldBe(9);
    }
}