// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Cli.Tests.Commands;

using System.Net;
using System.Text.Json;

using Shouldly;

using Xunit;

/// <summary>
/// Kiota-client tests for the new conversation + inbox wrappers added in #452 / #456.
/// Keep these as small, focused integration tests mirroring
/// <see cref="SpringApiClientTests"/> — each asserts the HTTP path, method,
/// body shape, and response parsing for a single wrapper method.
/// </summary>
public class ThreadClientTests
{
    private const string BaseUrl = "http://localhost:5000";

    [Fact]
    public async Task ListThreadsAsync_CallsCorrectEndpointAndParsesResponse()
    {
        var handler = new MockHttpMessageHandler(
            expectedPath: "/api/v1/tenant/threads",
            expectedMethod: HttpMethod.Get,
            responseBody: """[{"id":"c-1","participants":["agent://ada","human://savasp"],"status":"active","lastActivity":"2026-04-01T10:00:00Z","createdAt":"2026-04-01T09:55:00Z","eventCount":4,"origin":"agent://ada","summary":"Starting review."}]""");

        var httpClient = new HttpClient(handler);
        var client = new SpringApiClient(httpClient, BaseUrl);

        var result = await client.ListThreadsAsync(ct: TestContext.Current.CancellationToken);

        result.ShouldNotBeNull();
        result.Count.ShouldBe(1);
        result[0].Id.ShouldBe("c-1");
        result[0].Status.ShouldBe("active");
        result[0].Origin.ShouldBe("agent://ada");
        result[0].Participants.ShouldNotBeNull();
        result[0].Participants!.Count.ShouldBe(2);
        handler.WasCalled.ShouldBeTrue();
    }

    [Fact]
    public async Task ListThreadsAsync_WithFilters_ForwardsQueryParameters()
    {
        var handler = new MockHttpMessageHandler(
            expectedPath: "/api/v1/tenant/threads",
            expectedMethod: HttpMethod.Get,
            responseBody: "[]",
            validateQuery: query =>
            {
                query.ShouldContain("Unit=eng-team");
                query.ShouldContain("Status=active");
                query.ShouldContain("Limit=25");
            });

        var httpClient = new HttpClient(handler);
        var client = new SpringApiClient(httpClient, BaseUrl);

        var result = await client.ListThreadsAsync(
            unit: "eng-team",
            status: "active",
            limit: 25,
            ct: TestContext.Current.CancellationToken);

        result.ShouldBeEmpty();
        handler.WasCalled.ShouldBeTrue();
    }

    [Fact]
    public async Task GetThreadAsync_CallsCorrectEndpointAndParsesDetail()
    {
        var handler = new MockHttpMessageHandler(
            expectedPath: "/api/v1/tenant/threads/c-1",
            expectedMethod: HttpMethod.Get,
            responseBody: """{"summary":{"id":"c-1","participants":["agent://ada"],"status":"active","lastActivity":"2026-04-01T10:00:00Z","createdAt":"2026-04-01T09:55:00Z","eventCount":2,"origin":"agent://ada","summary":"Started"},"events":[{"id":"00000000-0000-0000-0000-000000000001","timestamp":"2026-04-01T09:55:00Z","source":"agent://ada","eventType":"ThreadStarted","severity":"Info","summary":"Started thread c-1"}]}""");

        var httpClient = new HttpClient(handler);
        var client = new SpringApiClient(httpClient, BaseUrl);

        var result = await client.GetThreadAsync("c-1", TestContext.Current.CancellationToken);

        result.ShouldNotBeNull();
        result.Summary.ShouldNotBeNull();
        result.Summary!.Id.ShouldBe("c-1");
        result.Events.ShouldNotBeNull();
        result.Events!.Count.ShouldBe(1);
        result.Events[0].EventType.ShouldBe("ThreadStarted");
        handler.WasCalled.ShouldBeTrue();
    }

    [Fact]
    public async Task SendThreadMessageAsync_PostsToCorrectEndpointWithWrappedBody()
    {
        var threadId = "c-1";
        var handler = new MockHttpMessageHandler(
            expectedPath: $"/api/v1/tenant/threads/{threadId}/messages",
            expectedMethod: HttpMethod.Post,
            responseBody: $$"""{"messageId":"{{Guid.NewGuid()}}","threadId":"{{threadId}}"}""",
            validateRequestBody: body =>
            {
                var json = JsonSerializer.Deserialize<JsonElement>(body);
                json.GetProperty("to").GetProperty("scheme").GetString().ShouldBe("agent");
                json.GetProperty("to").GetProperty("path").GetString().ShouldBe("ada");
                json.GetProperty("text").GetString().ShouldBe("Looks good — ship it.");
            });

        var httpClient = new HttpClient(handler);
        var client = new SpringApiClient(httpClient, BaseUrl);

        var result = await client.SendThreadMessageAsync(
            threadId, "agent", "ada", "Looks good — ship it.",
            TestContext.Current.CancellationToken);

        result.ShouldNotBeNull();
        result.MessageId.ShouldNotBeNull();
        result.ThreadId.ShouldBe(threadId);
        handler.WasCalled.ShouldBeTrue();
    }

    [Fact]
    public async Task ListInboxAsync_CallsCorrectEndpointAndParsesResponse()
    {
        var handler = new MockHttpMessageHandler(
            expectedPath: "/api/v1/tenant/inbox",
            expectedMethod: HttpMethod.Get,
            responseBody: """[{"threadId":"c-9","from":"agent://ada","human":"human://savasp","pendingSince":"2026-04-01T10:00:00Z","summary":"Needs your approval to merge."}]""");

        var httpClient = new HttpClient(handler);
        var client = new SpringApiClient(httpClient, BaseUrl);

        var result = await client.ListInboxAsync(TestContext.Current.CancellationToken);

        result.Count.ShouldBe(1);
        result[0].ThreadId.ShouldBe("c-9");
        result[0].From.ShouldBe("agent://ada");
        handler.WasCalled.ShouldBeTrue();
    }

    [Fact]
    public async Task ListInboxAsync_EmptyList_ReturnsEmpty()
    {
        var handler = new MockHttpMessageHandler(
            expectedPath: "/api/v1/tenant/inbox",
            expectedMethod: HttpMethod.Get,
            responseBody: "[]",
            returnStatusCode: HttpStatusCode.OK);

        var httpClient = new HttpClient(handler);
        var client = new SpringApiClient(httpClient, BaseUrl);

        var result = await client.ListInboxAsync(TestContext.Current.CancellationToken);

        result.ShouldBeEmpty();
        handler.WasCalled.ShouldBeTrue();
    }
}