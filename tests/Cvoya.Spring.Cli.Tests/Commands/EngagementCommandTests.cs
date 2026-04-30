// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Cli.Tests.Commands;

using System.CommandLine;
using System.Net;
using System.Text.Json;

using Cvoya.Spring.Cli.Commands;

using Shouldly;

using Xunit;

/// <summary>
/// Tests for <see cref="EngagementCommand"/> — command parsing and
/// API client integration for the E2.2 engagement primitive surface.
///
/// Three layers:
/// <list type="number">
///   <item>Parse tests — verify argument/option wiring so flag renames break CI.</item>
///   <item>Client tests — verify HTTP path, method, and query-string shape.</item>
///   <item>Output tests — verify the console output behaviour for key paths.</item>
/// </list>
/// </summary>
public class EngagementCommandTests
{
    private const string BaseUrl = "http://localhost:5000";

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    private static Option<string> CreateOutputOption() =>
        new Option<string>("--output", "-o")
        {
            Description = "Output format",
            DefaultValueFactory = _ => "table",
            Recursive = true,
        };

    private static (RootCommand Root, Command Engagement) BuildCommandTree()
    {
        var outputOption = CreateOutputOption();
        var engagement = EngagementCommand.Create(outputOption);
        var root = new RootCommand { Options = { outputOption } };
        root.Subcommands.Add(engagement);
        return (root, engagement);
    }

    // -----------------------------------------------------------------------
    // Parse tests — engagement list
    // -----------------------------------------------------------------------

    [Fact]
    public void EngagementList_NoFlags_ParsesCleanly()
    {
        var (root, _) = BuildCommandTree();
        var result = root.Parse("engagement list");
        result.Errors.ShouldBeEmpty();
    }

    [Fact]
    public void EngagementList_UnitFlag_ParsesUnitValue()
    {
        var (root, _) = BuildCommandTree();
        var result = root.Parse("engagement list --unit engineering-team");
        result.Errors.ShouldBeEmpty();
        result.GetValue<string>("--unit").ShouldBe("engineering-team");
    }

    [Fact]
    public void EngagementList_AgentFlag_ParsesAgentValue()
    {
        var (root, _) = BuildCommandTree();
        var result = root.Parse("engagement list --agent ada");
        result.Errors.ShouldBeEmpty();
        result.GetValue<string>("--agent").ShouldBe("ada");
    }

    [Fact]
    public void EngagementList_ParticipantFlag_ParsesParticipantAddress()
    {
        var (root, _) = BuildCommandTree();
        var result = root.Parse("engagement list --participant human://alice");
        result.Errors.ShouldBeEmpty();
        result.GetValue<string>("--participant").ShouldBe("human://alice");
    }

    [Fact]
    public void EngagementList_StatusFlag_ParsesStatus()
    {
        var (root, _) = BuildCommandTree();
        var result = root.Parse("engagement list --status active");
        result.Errors.ShouldBeEmpty();
        result.GetValue<string>("--status").ShouldBe("active");
    }

    [Fact]
    public void EngagementList_InvalidStatus_ProducesError()
    {
        var (root, _) = BuildCommandTree();
        var result = root.Parse("engagement list --status invalid");
        result.Errors.ShouldNotBeEmpty();
    }

    [Fact]
    public void EngagementList_LimitFlag_ParsesLimit()
    {
        var (root, _) = BuildCommandTree();
        var result = root.Parse("engagement list --limit 25");
        result.Errors.ShouldBeEmpty();
        result.GetValue<int?>("--limit").ShouldBe(25);
    }

    // -----------------------------------------------------------------------
    // Parse tests — engagement watch
    // -----------------------------------------------------------------------

    [Fact]
    public void EngagementWatch_RequiresId()
    {
        var (root, _) = BuildCommandTree();
        var result = root.Parse("engagement watch");
        result.Errors.ShouldNotBeEmpty();
    }

    [Fact]
    public void EngagementWatch_ParsesId()
    {
        var (root, _) = BuildCommandTree();
        var result = root.Parse("engagement watch thread-abc-123");
        result.Errors.ShouldBeEmpty();
        result.GetValue<string>("id").ShouldBe("thread-abc-123");
    }

    [Fact]
    public void EngagementWatch_SourceFlag_ParsesSource()
    {
        var (root, _) = BuildCommandTree();
        var result = root.Parse("engagement watch thread-abc-123 --source agent://ada");
        result.Errors.ShouldBeEmpty();
        result.GetValue<string>("--source").ShouldBe("agent://ada");
    }

    // -----------------------------------------------------------------------
    // Parse tests — engagement send
    // -----------------------------------------------------------------------

    [Fact]
    public void EngagementSend_RequiresIdAddressAndMessage()
    {
        var (root, _) = BuildCommandTree();
        // Missing message
        var result = root.Parse("engagement send thread-1 agent://ada");
        result.Errors.ShouldNotBeEmpty();
    }

    [Fact]
    public void EngagementSend_ParsesAllArgs()
    {
        var (root, _) = BuildCommandTree();
        var result = root.Parse("engagement send thread-1 agent://ada \"Review this PR\"");
        result.Errors.ShouldBeEmpty();
        result.GetValue<string>("id").ShouldBe("thread-1");
        result.GetValue<string>("address").ShouldBe("agent://ada");
        result.GetValue<string>("message").ShouldBe("Review this PR");
    }

    // -----------------------------------------------------------------------
    // Parse tests — engagement answer
    // -----------------------------------------------------------------------

    [Fact]
    public void EngagementAnswer_RequiresIdAddressAndAnswer()
    {
        var (root, _) = BuildCommandTree();
        var result = root.Parse("engagement answer thread-1 agent://ada");
        result.Errors.ShouldNotBeEmpty();
    }

    [Fact]
    public void EngagementAnswer_ParsesAllArgs()
    {
        var (root, _) = BuildCommandTree();
        var result = root.Parse("engagement answer thread-1 agent://ada \"Yes, merge it\"");
        result.Errors.ShouldBeEmpty();
        result.GetValue<string>("id").ShouldBe("thread-1");
        result.GetValue<string>("address").ShouldBe("agent://ada");
        result.GetValue<string>("answer").ShouldBe("Yes, merge it");
    }

    // -----------------------------------------------------------------------
    // Parse tests — engagement errors
    // -----------------------------------------------------------------------

    [Fact]
    public void EngagementErrors_RequiresId()
    {
        var (root, _) = BuildCommandTree();
        var result = root.Parse("engagement errors");
        result.Errors.ShouldNotBeEmpty();
    }

    [Fact]
    public void EngagementErrors_ParsesId()
    {
        var (root, _) = BuildCommandTree();
        var result = root.Parse("engagement errors thread-abc-456");
        result.Errors.ShouldBeEmpty();
        result.GetValue<string>("id").ShouldBe("thread-abc-456");
    }

    // -----------------------------------------------------------------------
    // API client tests — engagement list
    // -----------------------------------------------------------------------

    [Fact]
    public async Task EngagementListAsync_CallsThreadsEndpoint()
    {
        var handler = new MockHttpMessageHandler(
            expectedPath: "/api/v1/tenant/threads",
            expectedMethod: HttpMethod.Get,
            responseBody: "[]");

        var client = new SpringApiClient(new HttpClient(handler), BaseUrl);
        var result = await client.ListThreadsAsync(ct: TestContext.Current.CancellationToken);

        result.ShouldBeEmpty();
        handler.WasCalled.ShouldBeTrue();
    }

    [Fact]
    public async Task EngagementListAsync_WithUnit_ForwardsUnitQueryParam()
    {
        var handler = new MockHttpMessageHandler(
            expectedPath: "/api/v1/tenant/threads",
            expectedMethod: HttpMethod.Get,
            responseBody: "[]",
            validateQuery: q => q.ShouldContain("Unit=engineering-team"));

        var client = new SpringApiClient(new HttpClient(handler), BaseUrl);
        var result = await client.ListThreadsAsync(
            unit: "engineering-team",
            ct: TestContext.Current.CancellationToken);

        result.ShouldBeEmpty();
        handler.WasCalled.ShouldBeTrue();
    }

    [Fact]
    public async Task EngagementListAsync_WithAgent_ForwardsAgentQueryParam()
    {
        var handler = new MockHttpMessageHandler(
            expectedPath: "/api/v1/tenant/threads",
            expectedMethod: HttpMethod.Get,
            responseBody: "[]",
            validateQuery: q => q.ShouldContain("Agent=ada"));

        var client = new SpringApiClient(new HttpClient(handler), BaseUrl);
        var result = await client.ListThreadsAsync(
            agent: "ada",
            ct: TestContext.Current.CancellationToken);

        result.ShouldBeEmpty();
        handler.WasCalled.ShouldBeTrue();
    }

    [Fact]
    public async Task EngagementListAsync_WithParticipant_ForwardsParticipantQueryParam()
    {
        var handler = new MockHttpMessageHandler(
            expectedPath: "/api/v1/tenant/threads",
            expectedMethod: HttpMethod.Get,
            responseBody: "[]",
            validateQuery: q => q.ShouldContain("Participant=human%3A%2F%2Falice"));

        var client = new SpringApiClient(new HttpClient(handler), BaseUrl);
        var result = await client.ListThreadsAsync(
            participant: "human://alice",
            ct: TestContext.Current.CancellationToken);

        result.ShouldBeEmpty();
        handler.WasCalled.ShouldBeTrue();
    }

    // -----------------------------------------------------------------------
    // API client tests — engagement send / answer
    // -----------------------------------------------------------------------

    [Fact]
    public async Task EngagementSendAsync_PostsToCorrectEndpoint()
    {
        var threadId = "t-engagement-1";
        var handler = new MockHttpMessageHandler(
            expectedPath: $"/api/v1/tenant/threads/{threadId}/messages",
            expectedMethod: HttpMethod.Post,
            responseBody: $$"""{"messageId":"{{Guid.NewGuid()}}","threadId":"{{threadId}}"}""",
            validateRequestBody: body =>
            {
                var json = JsonSerializer.Deserialize<JsonElement>(body);
                json.GetProperty("to").GetProperty("scheme").GetString().ShouldBe("agent");
                json.GetProperty("to").GetProperty("path").GetString().ShouldBe("ada");
                json.GetProperty("text").GetString().ShouldBe("Please review the latest commit.");
            });

        var client = new SpringApiClient(new HttpClient(handler), BaseUrl);
        var result = await client.SendThreadMessageAsync(
            threadId, "agent", "ada", "Please review the latest commit.",
            ct: TestContext.Current.CancellationToken);

        result.ThreadId.ShouldBe(threadId);
        handler.WasCalled.ShouldBeTrue();
    }

    [Fact]
    public async Task EngagementAnswerAsync_PostsToSameEndpointAsSend()
    {
        // answer and send both call SendThreadMessageAsync — they share the
        // endpoint; the semantic distinction is expressed via kind (#1421).
        var threadId = "t-engagement-2";
        var handler = new MockHttpMessageHandler(
            expectedPath: $"/api/v1/tenant/threads/{threadId}/messages",
            expectedMethod: HttpMethod.Post,
            responseBody: $$"""{"messageId":"{{Guid.NewGuid()}}","threadId":"{{threadId}}"}""",
            validateRequestBody: body =>
            {
                var json = JsonSerializer.Deserialize<JsonElement>(body);
                json.GetProperty("text").GetString().ShouldBe("Yes, proceed with the merge.");
            });

        var client = new SpringApiClient(new HttpClient(handler), BaseUrl);
        var result = await client.SendThreadMessageAsync(
            threadId, "agent", "ada", "Yes, proceed with the merge.",
            ct: TestContext.Current.CancellationToken);

        result.ThreadId.ShouldBe(threadId);
        handler.WasCalled.ShouldBeTrue();
    }

    // -----------------------------------------------------------------------
    // API client tests — engagement errors (via GetThreadAsync)
    // -----------------------------------------------------------------------

    [Fact]
    public async Task EngagementErrorsAsync_FetchesThreadAndFiltersErrorEvents()
    {
        var threadId = "t-errors-1";
        var handler = new MockHttpMessageHandler(
            expectedPath: $"/api/v1/tenant/threads/{threadId}",
            expectedMethod: HttpMethod.Get,
            responseBody: $$"""
            {
              "summary": {
                "id": "{{threadId}}",
                "participants": ["agent://ada"],
                "status": "active",
                "lastActivity": "2026-04-01T10:00:00Z",
                "createdAt": "2026-04-01T09:55:00Z",
                "eventCount": 3,
                "origin": "agent://ada",
                "summary": "PR review"
              },
              "events": [
                {"id":"{{Guid.NewGuid()}}","timestamp":"2026-04-01T09:55:00Z","source":"agent://ada","eventType":"ThreadStarted","severity":"Info","summary":"Started"},
                {"id":"{{Guid.NewGuid()}}","timestamp":"2026-04-01T09:58:00Z","source":"agent://ada","eventType":"ErrorOccurred","severity":"Error","summary":"Dispatch failed: container exit 125"},
                {"id":"{{Guid.NewGuid()}}","timestamp":"2026-04-01T10:00:00Z","source":"agent://ada","eventType":"MessageReceived","severity":"Info","summary":"Retry succeeded"}
              ]
            }
            """);

        var client = new SpringApiClient(new HttpClient(handler), BaseUrl);
        var detail = await client.GetThreadAsync(threadId, TestContext.Current.CancellationToken);

        detail.ShouldNotBeNull();
        detail.Events.ShouldNotBeNull();
        detail.Events!.Count.ShouldBe(3);

        // Client-side error filter (mirrors EngagementCommand.CreateErrorsCommand).
        var errors = detail.Events
            .Where(e =>
                string.Equals(e.EventType, "ErrorOccurred", StringComparison.Ordinal)
                || string.Equals(e.Severity, "Error", StringComparison.OrdinalIgnoreCase))
            .ToList();

        errors.Count.ShouldBe(1);
        errors[0].EventType.ShouldBe("ErrorOccurred");
        (errors[0].Summary ?? string.Empty).ShouldContain("Dispatch failed");
        handler.WasCalled.ShouldBeTrue();
    }

    [Fact]
    public async Task EngagementErrorsAsync_NoErrors_ReturnsEmptyFilter()
    {
        var threadId = "t-no-errors";
        var handler = new MockHttpMessageHandler(
            expectedPath: $"/api/v1/tenant/threads/{threadId}",
            expectedMethod: HttpMethod.Get,
            responseBody: $$"""
            {
              "summary": {
                "id": "{{threadId}}",
                "participants": ["agent://ada"],
                "status": "active",
                "lastActivity": "2026-04-01T10:00:00Z",
                "createdAt": "2026-04-01T09:55:00Z",
                "eventCount": 1,
                "origin": "agent://ada",
                "summary": "All good"
              },
              "events": [
                {"id":"{{Guid.NewGuid()}}","timestamp":"2026-04-01T09:55:00Z","source":"agent://ada","eventType":"MessageReceived","severity":"Info","summary":"OK"}
              ]
            }
            """);

        var client = new SpringApiClient(new HttpClient(handler), BaseUrl);
        var detail = await client.GetThreadAsync(threadId, TestContext.Current.CancellationToken);

        var errors = detail.Events!
            .Where(e =>
                string.Equals(e.EventType, "ErrorOccurred", StringComparison.Ordinal)
                || string.Equals(e.Severity, "Error", StringComparison.OrdinalIgnoreCase))
            .ToList();

        errors.ShouldBeEmpty();
        handler.WasCalled.ShouldBeTrue();
    }

    // -----------------------------------------------------------------------
    // API client tests — StreamEngagementAsync
    // -----------------------------------------------------------------------

    [Fact]
    public async Task StreamEngagementAsync_FiltersEventsByThreadId()
    {
        // The method should forward only lines whose JSON contains the threadId.
        // The server-side ?thread= filter handles this on modern servers; the
        // client-side fallback catches any that slip through on older servers.
        var threadId = "t-stream-1";
        var otherThread = "t-stream-other";

        var sseBody =
            $"data: {{\"timestamp\":\"2026-04-01T10:00:00Z\",\"correlationId\":\"{threadId}\",\"eventType\":\"MessageReceived\",\"severity\":\"Info\",\"summary\":\"Hello\",\"source\":{{\"scheme\":\"agent\",\"path\":\"ada\"}}}}\n\n" +
            $"data: {{\"timestamp\":\"2026-04-01T10:01:00Z\",\"correlationId\":\"{otherThread}\",\"eventType\":\"MessageReceived\",\"severity\":\"Info\",\"summary\":\"Other\",\"source\":{{\"scheme\":\"agent\",\"path\":\"bob\"}}}}\n\n";

        var handler = new SseHttpMessageHandler(
            expectedPath: "/api/v1/tenant/activity/stream",
            sseContent: sseBody);

        var client = new SpringApiClient(new HttpClient(handler), BaseUrl);

        var received = new List<string>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        await client.StreamEngagementAsync(
            threadId: threadId,
            source: null,
            onEvent: line => received.Add(line),
            ct: cts.Token);

        // Only the line referencing threadId should have been forwarded.
        received.Count.ShouldBe(1);
        received[0].ShouldContain(threadId);
        received[0].ShouldNotContain(otherThread);
        handler.WasCalled.ShouldBeTrue();
    }

    [Fact]
    public async Task StreamEngagementAsync_SendsThreadQueryParam()
    {
        // StreamEngagementAsync must send ?thread=<id> to the server (#1421).
        var threadId = "t-stream-qp-1";
        var handler = new SseHttpMessageHandler(
            expectedPath: "/api/v1/tenant/activity/stream",
            sseContent: string.Empty,
            validateQuery: q =>
            {
                q.ShouldContain($"thread={Uri.EscapeDataString(threadId)}");
            });

        var client = new SpringApiClient(new HttpClient(handler), BaseUrl);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await client.StreamEngagementAsync(
            threadId: threadId,
            source: null,
            onEvent: _ => { },
            ct: cts.Token);

        handler.WasCalled.ShouldBeTrue();
    }

    [Fact]
    public async Task StreamEngagementAsync_WithSource_ForwardsSourceQueryParam()
    {
        var threadId = "t-stream-2";
        var handler = new SseHttpMessageHandler(
            expectedPath: "/api/v1/tenant/activity/stream",
            sseContent: string.Empty,
            validateQuery: q =>
            {
                q.ShouldContain("source=agent%3A%2F%2Fada");
                q.ShouldContain($"thread={Uri.EscapeDataString(threadId)}");
            });

        var client = new SpringApiClient(new HttpClient(handler), BaseUrl);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await client.StreamEngagementAsync(
            threadId: threadId,
            source: "agent://ada",
            onEvent: _ => { },
            ct: cts.Token);

        handler.WasCalled.ShouldBeTrue();
    }

    // -----------------------------------------------------------------------
    // API client tests — kind discriminator (#1421)
    // -----------------------------------------------------------------------

    [Fact]
    public async Task EngagementSendAsync_SendsKindInformation()
    {
        // engagement send must set kind=information on the request body.
        var threadId = "t-kind-send";
        var handler = new MockHttpMessageHandler(
            expectedPath: $"/api/v1/tenant/threads/{threadId}/messages",
            expectedMethod: HttpMethod.Post,
            responseBody: $$"""{"messageId":"{{Guid.NewGuid()}}","threadId":"{{threadId}}","responsePayload":null,"kind":"information"}""",
            validateRequestBody: body =>
            {
                var json = JsonSerializer.Deserialize<JsonElement>(body);
                json.GetProperty("kind").GetString().ShouldBe("information");
            });

        var client = new SpringApiClient(new HttpClient(handler), BaseUrl);
        await client.SendThreadMessageAsync(threadId, "agent", "ada", "Hello", kind: "information",
            ct: TestContext.Current.CancellationToken);

        handler.WasCalled.ShouldBeTrue();
    }

    [Fact]
    public async Task EngagementAnswerAsync_SendsKindAnswer()
    {
        // engagement answer must set kind=answer on the request body (#1421).
        var threadId = "t-kind-answer";
        var handler = new MockHttpMessageHandler(
            expectedPath: $"/api/v1/tenant/threads/{threadId}/messages",
            expectedMethod: HttpMethod.Post,
            responseBody: $$"""{"messageId":"{{Guid.NewGuid()}}","threadId":"{{threadId}}","responsePayload":null,"kind":"answer"}""",
            validateRequestBody: body =>
            {
                var json = JsonSerializer.Deserialize<JsonElement>(body);
                json.GetProperty("kind").GetString().ShouldBe("answer");
            });

        var client = new SpringApiClient(new HttpClient(handler), BaseUrl);
        await client.SendThreadMessageAsync(threadId, "agent", "ada", "Yes, proceed", kind: "answer",
            ct: TestContext.Current.CancellationToken);

        handler.WasCalled.ShouldBeTrue();
    }
}

// ---------------------------------------------------------------------------
// SSE test double — returns a fixed SSE body and completes.
// ---------------------------------------------------------------------------

/// <summary>
/// Test double that serves a canned SSE body as a text/event-stream response,
/// then closes the connection so the reader loop terminates.
/// </summary>
internal sealed class SseHttpMessageHandler(
    string expectedPath,
    string sseContent,
    Action<string>? validateQuery = null) : HttpMessageHandler
{
    public bool WasCalled { get; private set; }

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        WasCalled = true;
        request.RequestUri!.AbsolutePath.ShouldBe(expectedPath);

        if (validateQuery is not null)
        {
            validateQuery(request.RequestUri.Query);
        }

        var response = new HttpResponseMessage(System.Net.HttpStatusCode.OK);
        response.Content = new StringContent(
            sseContent,
            System.Text.Encoding.UTF8,
            "text/event-stream");

        return Task.FromResult(response);
    }
}