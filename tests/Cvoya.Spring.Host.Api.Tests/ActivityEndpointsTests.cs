// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Api.Tests;

using System.Net;
using System.Net.Http.Json;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Text.Json;

using Cvoya.Spring.Core.Capabilities;
using Cvoya.Spring.Core.Directory;
using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Core.Observability;
using Cvoya.Spring.Dapr.Actors;
using Cvoya.Spring.Dapr.Auth;

using NSubstitute;

using Shouldly;

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

        var response = await _client.GetAsync("/api/v1/tenant/activity", ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var result = await response.Content.ReadFromJsonAsync<ActivityQueryResult>(ct);
        result.ShouldNotBeNull();
        result!.TotalCount.ShouldBe(0);
        result.Items.ShouldBeEmpty();
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

        var response = await _client.GetAsync("/api/v1/tenant/activity?source=agent://test&eventType=TaskCompleted&page=1&pageSize=10", ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var result = await response.Content.ReadFromJsonAsync<ActivityQueryResult>(ct);
        result.ShouldNotBeNull();
        result!.TotalCount.ShouldBe(1);
        result.Items.Count().ShouldBe(1);

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
        _factory.ActivityEventBus.ActivityStream.Returns(Observable.Never<ActivityEvent>());

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(3));

        var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/tenant/activity/stream");

        try
        {
            var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            response.StatusCode.ShouldBe(HttpStatusCode.OK);
            response.Content.Headers.ContentType?.MediaType.ShouldBe("text/event-stream");
        }
        catch (OperationCanceledException)
        {
            // Expected — we cancel the SSE stream after reading headers.
        }
    }

    /// <summary>
    /// Closes the #391 acceptance: "Emit → subscribe → SSE chain covered by
    /// an integration test that asserts every event type reaches the
    /// endpoint." Publishes one event for every value of
    /// <see cref="ActivityEventType"/> (excluding values the bus never
    /// produces in isolation) and asserts each is serialised into the SSE
    /// response body.
    /// </summary>
    [Fact]
    public async Task StreamActivity_EveryEventTypeReachesTheSseEndpoint()
    {
        var ct = TestContext.Current.CancellationToken;

        using var subject = new Subject<ActivityEvent>();
        _factory.ActivityEventBus.ActivityStream.Returns(subject);

        // Build one event per value of ActivityEventType; every enum value is
        // covered so the next contributor who appends an entry can't silently
        // regress the SSE chain.
        var eventTypes = Enum.GetValues<ActivityEventType>();
        var expected = new List<ActivityEvent>();
        foreach (var type in eventTypes)
        {
            expected.Add(new ActivityEvent(
                Id: Guid.NewGuid(),
                Timestamp: DateTimeOffset.UtcNow,
                Source: new Address("agent", "agent-int-test"),
                EventType: type,
                Severity: ActivitySeverity.Info,
                Summary: $"{type}-summary"));
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(10));

        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/tenant/activity/stream");
        using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        await using var stream = await response.Content.ReadAsStreamAsync(cts.Token);
        using var reader = new StreamReader(stream);

        // Feed every event through the mocked bus. The subscription is hot —
        // the endpoint has already subscribed by the time HEADERS are flushed.
        foreach (var evt in expected)
        {
            subject.OnNext(evt);
        }

        var received = new HashSet<ActivityEventType>();

        // Read lines with a per-line timeout via a nested cancellation so the
        // final "no more events coming" state is detected without aborting
        // the request and bubbling an IOException through StreamReader.
        while (received.Count < expected.Count)
        {
            using var lineCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
            lineCts.CancelAfter(TimeSpan.FromSeconds(2));

            string? line;
            try
            {
                line = await reader.ReadLineAsync(lineCts.Token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (IOException)
            {
                // TestHost surfaces cancellation as IOException("client aborted") once the
                // outer test timeout or the per-line CTS trips. Either signals "no more
                // events coming" from the SSE stream for the purposes of this test.
                break;
            }

            if (string.IsNullOrEmpty(line) || !line.StartsWith("data: ", StringComparison.Ordinal))
            {
                continue;
            }

            var json = line["data: ".Length..];
            using var doc = JsonDocument.Parse(json);
            JsonElement prop = default;
            var hasProp =
                doc.RootElement.TryGetProperty("eventType", out prop) ||
                doc.RootElement.TryGetProperty("EventType", out prop);
            if (!hasProp)
            {
                continue;
            }

            if (prop.ValueKind == JsonValueKind.Number &&
                prop.TryGetInt32(out var ordinal) &&
                Enum.IsDefined(typeof(ActivityEventType), ordinal))
            {
                received.Add((ActivityEventType)ordinal);
            }
            else if (prop.ValueKind == JsonValueKind.String &&
                Enum.TryParse<ActivityEventType>(prop.GetString(), out var parsed))
            {
                received.Add(parsed);
            }
        }

        // Every value in the enum must have made it through. This asserts the
        // contract that the SSE relay is type-agnostic — a new event type
        // added to the enum automatically flows without wiring changes.
        foreach (var type in eventTypes)
        {
            received.ShouldContain(type,
                $"SSE relay did not deliver {type} — every enum value must reach subscribers.");
        }
    }

    /// <summary>
    /// #987: the portal tenant tree only carries unit/agent slugs, so the
    /// Activity tab queries with `source=unit:<slug>`. Events are stored
    /// with `source=unit:<actorId>`, so without normalization every
    /// slug-based query returned an empty page. This test pins the
    /// server-side rewrite: the query service sees the actor id, and the
    /// slug-based URL reaches the same row set that a direct
    /// `source=unit:<actorId>` URL would.
    /// </summary>
    [Fact]
    public async Task QueryActivity_WithUnitSlugSource_ResolvesSlugToActorId()
    {
        var ct = TestContext.Current.CancellationToken;
        const string slug = "portal-scratch-1";
        const string actorId = "2d3e4f56-7890-4abc-8def-0123456789ab";

        _factory.DirectoryService.ResolveAsync(new Address("unit", slug), Arg.Any<CancellationToken>())
            .Returns(new DirectoryEntry(
                new Address("unit", slug),
                actorId,
                "Portal Scratch 1",
                string.Empty,
                Role: null,
                DateTimeOffset.UtcNow));

        var items = new List<ActivityQueryResult.Item>
        {
            new(Guid.NewGuid(), $"unit:{actorId}", "MessageReceived", "Info", "msg", null, null, DateTimeOffset.UtcNow)
        };
        _factory.ActivityQueryService.QueryAsync(Arg.Any<ActivityQueryParameters>(), Arg.Any<CancellationToken>())
            .Returns(new ActivityQueryResult(items, 1, 1, 50));

        var response = await _client.GetAsync($"/api/v1/tenant/activity?source=unit:{slug}&pageSize=5", ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        await _factory.ActivityQueryService.Received(1).QueryAsync(
            Arg.Is<ActivityQueryParameters>(p => p.Source == $"unit:{actorId}"),
            Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// Sibling to the unit slug test — agent Activity tabs hit the same
    /// root cause per the issue, so the rewrite must cover `agent:` too.
    /// </summary>
    [Fact]
    public async Task QueryActivity_WithAgentSlugSource_ResolvesSlugToActorId()
    {
        var ct = TestContext.Current.CancellationToken;
        const string slug = "ada";
        const string actorId = "00aa11bb-22cc-4dd5-e6f7-8901234567ef";

        _factory.DirectoryService.ResolveAsync(new Address("agent", slug), Arg.Any<CancellationToken>())
            .Returns(new DirectoryEntry(
                new Address("agent", slug),
                actorId,
                "Ada",
                string.Empty,
                Role: null,
                DateTimeOffset.UtcNow));

        _factory.ActivityQueryService.QueryAsync(Arg.Any<ActivityQueryParameters>(), Arg.Any<CancellationToken>())
            .Returns(new ActivityQueryResult([], 0, 1, 50));

        var response = await _client.GetAsync($"/api/v1/tenant/activity?source=agent:{slug}", ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        await _factory.ActivityQueryService.Received(1).QueryAsync(
            Arg.Is<ActivityQueryParameters>(p => p.Source == $"agent:{actorId}"),
            Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// Unknown slug (deleted unit, typo) must not 400 — the platform had
    /// "empty page" semantics before the fix, and the issue explicitly
    /// asks to preserve that shape.
    /// </summary>
    [Fact]
    public async Task QueryActivity_WithUnknownUnitSlug_ReturnsEmptyNotError()
    {
        var ct = TestContext.Current.CancellationToken;

        _factory.DirectoryService.ResolveAsync(new Address("unit", "ghost"), Arg.Any<CancellationToken>())
            .Returns((DirectoryEntry?)null);

        _factory.ActivityQueryService.QueryAsync(Arg.Any<ActivityQueryParameters>(), Arg.Any<CancellationToken>())
            .Returns(new ActivityQueryResult([], 0, 1, 50));

        var response = await _client.GetAsync("/api/v1/tenant/activity?source=unit:ghost", ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var result = await response.Content.ReadFromJsonAsync<ActivityQueryResult>(ct);
        result.ShouldNotBeNull();
        result!.TotalCount.ShouldBe(0);
    }

    /// <summary>
    /// Closes the #391 acceptance: "An observation subscription test verifies
    /// cross-agent permission enforcement." The caller requests a unit-scoped
    /// stream for a unit they have no permission on; the endpoint must refuse
    /// at subscribe time (403) rather than opening an empty stream.
    /// </summary>
    [Fact]
    public async Task StreamActivity_UnitScoped_WithoutPermission_Returns403()
    {
        var ct = TestContext.Current.CancellationToken;

        var permissionService = (IPermissionService)_factory.Services
            .GetService(typeof(IPermissionService))!;
        permissionService.ResolveEffectivePermissionAsync(
                Arg.Any<string>(), "locked-unit", Arg.Any<CancellationToken>())
            .Returns((PermissionLevel?)null);

        var response = await _client.GetAsync("/api/v1/tenant/activity/stream?unitId=locked-unit", ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    /// <summary>
    /// Viewer or higher on the target unit flows to a real event stream — the
    /// endpoint resolves permission once at subscribe time, then hands the
    /// unit-scoped observable to the SSE relay.
    /// </summary>
    [Fact]
    public async Task StreamActivity_UnitScoped_WithPermission_OpensSseStream()
    {
        var ct = TestContext.Current.CancellationToken;

        var permissionService = (IPermissionService)_factory.Services
            .GetService(typeof(IPermissionService))!;
        permissionService.ResolveEffectivePermissionAsync(
                Arg.Any<string>(), "open-unit", Arg.Any<CancellationToken>())
            .Returns(PermissionLevel.Viewer);

        _factory.UnitActivityObservable.GetStreamAsync("open-unit", Arg.Any<CancellationToken>())
            .Returns(Observable.Never<ActivityEvent>());

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(3));

        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/tenant/activity/stream?unitId=open-unit");

        try
        {
            var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            response.StatusCode.ShouldBe(HttpStatusCode.OK);
            response.Content.Headers.ContentType?.MediaType.ShouldBe("text/event-stream");
        }
        catch (OperationCanceledException)
        {
            // Expected — we close the stream after asserting headers.
        }

        await _factory.UnitActivityObservable.Received(1)
            .GetStreamAsync("open-unit", Arg.Any<CancellationToken>());
    }
}