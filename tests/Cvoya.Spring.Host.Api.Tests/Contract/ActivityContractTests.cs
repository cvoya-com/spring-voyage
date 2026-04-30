// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Api.Tests.Contract;

using System.Net;

using Cvoya.Spring.Core.Observability;

using NSubstitute;

using Shouldly;

using Xunit;

/// <summary>
/// Semantic contract tests for the <c>/api/v1/tenant/activity</c> surface
/// (closes #1255 / C1.3). Validates that the query-activity response body
/// matches the committed openapi.json shape so semantic drift (required
/// field dropped, enum removed, pagination wrapper renamed) fails CI.
/// </summary>
public class ActivityContractTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public ActivityContractTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task QueryActivity_HappyPath_MatchesContract()
    {
        var ct = TestContext.Current.CancellationToken;
        var now = DateTimeOffset.UtcNow;
        _factory.ActivityQueryService
            .QueryAsync(Arg.Any<ActivityQueryParameters>(), Arg.Any<CancellationToken>())
            .Returns(new ActivityQueryResult(
                new[]
                {
                    new ActivityQueryResult.Item(
                        Guid.NewGuid(),
                        "agent://contract-bot",
                        "MessageReceived",
                        "Info",
                        "Contract test event",
                        "contract-thread-1",
                        Cost: 0m,
                        now),
                },
                TotalCount: 1,
                Page: 1,
                PageSize: 50));

        var response = await _client.GetAsync("/api/v1/tenant/activity", ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var body = await response.Content.ReadAsStringAsync(ct);
        OpenApiContract.AssertResponse("/api/v1/tenant/activity", "get", "200", body);
    }

    [Fact]
    public async Task QueryActivity_NullCost_MatchesContract()
    {
        // Non-cost events (e.g. MessageReceived) carry a null Cost — the
        // openapi.json declares cost as optional/nullable (closes #1367).
        var ct = TestContext.Current.CancellationToken;
        var now = DateTimeOffset.UtcNow;
        _factory.ActivityQueryService
            .QueryAsync(Arg.Any<ActivityQueryParameters>(), Arg.Any<CancellationToken>())
            .Returns(new ActivityQueryResult(
                new[]
                {
                    new ActivityQueryResult.Item(
                        Guid.NewGuid(),
                        "agent://contract-bot",
                        "MessageReceived",
                        "Info",
                        "Non-cost event",
                        "contract-thread-2",
                        Cost: null,
                        now),
                },
                TotalCount: 1,
                Page: 1,
                PageSize: 50));

        var response = await _client.GetAsync("/api/v1/tenant/activity", ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var body = await response.Content.ReadAsStringAsync(ct);
        OpenApiContract.AssertResponse("/api/v1/tenant/activity", "get", "200", body);
    }

    [Fact]
    public async Task QueryActivity_EmptyPage_MatchesContract()
    {
        var ct = TestContext.Current.CancellationToken;
        _factory.ActivityQueryService
            .QueryAsync(Arg.Any<ActivityQueryParameters>(), Arg.Any<CancellationToken>())
            .Returns(new ActivityQueryResult(
                Array.Empty<ActivityQueryResult.Item>(),
                TotalCount: 0,
                Page: 1,
                PageSize: 50));

        var response = await _client.GetAsync("/api/v1/tenant/activity?page=1&pageSize=50", ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var body = await response.Content.ReadAsStringAsync(ct);
        OpenApiContract.AssertResponse("/api/v1/tenant/activity", "get", "200", body);
    }
}