// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Api.Tests;

using System.Net;
using System.Net.Http.Json;

using Cvoya.Spring.Core.Observability;

using FluentAssertions;

using NSubstitute;

using Xunit;

public class ActivityEndpointsTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public ActivityEndpointsTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task QueryActivity_NoFilters_ReturnsEmptyResult()
    {
        var ct = TestContext.Current.CancellationToken;
        var emptyResult = new ActivityQueryResult([], 0, 1, 50);
        _factory.ActivityQueryService.QueryAsync(Arg.Any<ActivityQueryParameters>(), Arg.Any<CancellationToken>())
            .Returns(emptyResult);

        var response = await _client.GetAsync("/api/v1/activity", ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var result = await response.Content.ReadFromJsonAsync<ActivityQueryResult>(ct);
        result.Should().NotBeNull();
        result!.TotalCount.Should().Be(0);
        result.Items.Should().BeEmpty();
    }

    [Fact]
    public async Task QueryActivity_WithFilters_PassesParametersToService()
    {
        var ct = TestContext.Current.CancellationToken;
        var items = new List<ActivityQueryResult.Item>
        {
            new(Guid.NewGuid(), "agent://test", "TaskCompleted", "Info", "Task done", null, 1.5m, DateTimeOffset.UtcNow)
        };
        var filteredResult = new ActivityQueryResult(items, 1, 1, 10);
        _factory.ActivityQueryService.QueryAsync(Arg.Any<ActivityQueryParameters>(), Arg.Any<CancellationToken>())
            .Returns(filteredResult);

        var response = await _client.GetAsync("/api/v1/activity?source=agent://test&eventType=TaskCompleted&page=1&pageSize=10", ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var result = await response.Content.ReadFromJsonAsync<ActivityQueryResult>(ct);
        result.Should().NotBeNull();
        result!.TotalCount.Should().Be(1);
        result.Items.Should().HaveCount(1);

        await _factory.ActivityQueryService.Received(1).QueryAsync(
            Arg.Is<ActivityQueryParameters>(p =>
                p.Source == "agent://test" &&
                p.EventType == "TaskCompleted" &&
                p.Page == 1 &&
                p.PageSize == 10),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task StreamActivity_ReturnsCorrectContentType()
    {
        var ct = TestContext.Current.CancellationToken;
        _factory.ActivityQueryService.GetRecentAsync(Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns(new List<ActivityQueryResult.Item>());

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(3));

        var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/activity/stream");

        try
        {
            var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            response.Content.Headers.ContentType?.MediaType.Should().Be("text/event-stream");
        }
        catch (OperationCanceledException)
        {
            // Expected — we cancel the SSE stream after reading headers.
        }
    }
}