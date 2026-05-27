// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Api.Tests.Contract;

using System.Net;

using Cvoya.Spring.Core.Identifiers;
using Cvoya.Spring.Core.Observability;

using NSubstitute;

using Shouldly;

using Xunit;

/// <summary>
/// Contract tests for the Interactions visualization endpoints (#2867).
/// Verifies the response body shape matches the committed
/// <c>openapi.json</c> contract on the happy path and on the truncation
/// branch.
/// </summary>
public class InteractionsContractTests : IClassFixture<InteractionsEndpointsTests.Factory>
{
    private readonly InteractionsEndpointsTests.Factory _factory;
    private readonly HttpClient _client;

    public InteractionsContractTests(InteractionsEndpointsTests.Factory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task GetInteractions_HappyPath_MatchesContract()
    {
        var ct = TestContext.Current.CancellationToken;
        var now = DateTimeOffset.UtcNow;

        var fromId = Guid.NewGuid();
        var toId = Guid.NewGuid();
        var graph = new InteractionsGraph(
            Nodes: new List<InteractionsNode>
            {
                new(GuidFormatter.Format(fromId), "agent", "Ada", Sent: 3, Received: 1),
                new(GuidFormatter.Format(toId), "unit", "Engineering", Sent: 1, Received: 3),
            },
            Edges: new List<InteractionsEdge>
            {
                new(GuidFormatter.Format(fromId), GuidFormatter.Format(toId),
                    Count: 3, FirstAt: now.AddMinutes(-5), LastAt: now,
                    Channels: new[] { "unit" }),
            },
            Timeline: new List<InteractionsTimelineBucket>
            {
                new(now, Sent: 3, ByKind: new Dictionary<string, long>
                {
                    ["agent"] = 3, ["unit"] = 0, ["human"] = 0, ["connector"] = 0,
                }),
            },
            Truncated: null);

        _factory.InteractionsQueryService
            .GetAsync(Arg.Any<InteractionsQueryFilters>(), Arg.Any<CancellationToken>())
            .Returns(graph);

        var response = await _client.GetAsync("/api/v1/tenant/observation/interactions", ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync(ct);
        OpenApiContract.AssertResponse(
            "/api/v1/tenant/observation/interactions", "get", "200", body);
    }

    [Fact]
    public async Task GetInteractions_WithTruncation_MatchesContract()
    {
        var ct = TestContext.Current.CancellationToken;
        var graph = new InteractionsGraph(
            Nodes: Array.Empty<InteractionsNode>(),
            Edges: Array.Empty<InteractionsEdge>(),
            Timeline: Array.Empty<InteractionsTimelineBucket>(),
            Truncated: new InteractionsTruncation(Total: 200, Kept: 50));

        _factory.InteractionsQueryService
            .GetAsync(Arg.Any<InteractionsQueryFilters>(), Arg.Any<CancellationToken>())
            .Returns(graph);

        var response = await _client.GetAsync("/api/v1/tenant/observation/interactions", ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync(ct);
        OpenApiContract.AssertResponse(
            "/api/v1/tenant/observation/interactions", "get", "200", body);
    }
}
