// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Api.Tests;

using System.Net;
using System.Net.Http.Json;
using System.Reactive.Subjects;
using System.Text.Json;

using Cvoya.Spring.Core.Capabilities;
using Cvoya.Spring.Core.Identifiers;
using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Core.Observability;
using Cvoya.Spring.Host.Api.Models;

using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

using NSubstitute;
using NSubstitute.ClearExtensions;

using Shouldly;

using Xunit;

/// <summary>
/// Integration tests for the tenant-wide Interactions endpoints (#2867).
/// The query service is mocked so these tests stay focused on the HTTP
/// surface — parameter binding, default window, cap parsing, response
/// shape — while the aggregation correctness lives next to the EF code
/// in <c>InteractionsQueryServiceTests</c>. Role-policy gating is
/// verified by <c>RolePoliciesTests</c>; here the OSS overlay's
/// "every authenticated caller gets every role" path lets the test
/// client pass through to the endpoint.
/// </summary>
public class InteractionsEndpointsTests : IClassFixture<InteractionsEndpointsTests.Factory>
{
    private readonly Factory _factory;
    private readonly HttpClient _client;

    public InteractionsEndpointsTests(Factory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task GetInteractions_NoParams_AppliesDefaultWindowAndCap()
    {
        // No query params → the host applies the "now − 10 minutes ..
        // now" default window and a cap of 50. We assert the service
        // receives a 10-minute window and a cap of 50 — the exact
        // boundaries shift with wall clock, so we verify the *span*.
        var ct = TestContext.Current.CancellationToken;
        _factory.InteractionsQueryService.ClearSubstitute();
        _factory.InteractionsQueryService
            .GetAsync(Arg.Any<InteractionsQueryFilters>(), Arg.Any<CancellationToken>())
            .Returns(EmptyGraph());

        var response = await _client.GetAsync("/api/v1/tenant/observation/interactions", ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        await _factory.InteractionsQueryService.Received(1).GetAsync(
            Arg.Is<InteractionsQueryFilters>(f =>
                f.Cap == 50 &&
                f.Neighbours == 2 &&
                f.Bucket == InteractionsBucket.Hour &&
                f.Unit == null &&
                f.Participant == null &&
                Math.Abs((f.Until - f.Since).TotalMinutes - 10) < 1.0),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetInteractions_CapNone_DisablesTruncation()
    {
        var ct = TestContext.Current.CancellationToken;
        _factory.InteractionsQueryService.ClearSubstitute();
        _factory.InteractionsQueryService
            .GetAsync(Arg.Any<InteractionsQueryFilters>(), Arg.Any<CancellationToken>())
            .Returns(EmptyGraph());

        var response = await _client.GetAsync("/api/v1/tenant/observation/interactions?cap=none", ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        await _factory.InteractionsQueryService.Received(1).GetAsync(
            Arg.Is<InteractionsQueryFilters>(f => f.Cap == null),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetInteractions_CapInteger_ForwardsValue()
    {
        var ct = TestContext.Current.CancellationToken;
        _factory.InteractionsQueryService.ClearSubstitute();
        _factory.InteractionsQueryService
            .GetAsync(Arg.Any<InteractionsQueryFilters>(), Arg.Any<CancellationToken>())
            .Returns(EmptyGraph());

        var response = await _client.GetAsync("/api/v1/tenant/observation/interactions?cap=10", ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        await _factory.InteractionsQueryService.Received(1).GetAsync(
            Arg.Is<InteractionsQueryFilters>(f => f.Cap == 10),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetInteractions_NeighboursClamped_OutOfRangeFallsToDefault()
    {
        var ct = TestContext.Current.CancellationToken;
        _factory.InteractionsQueryService.ClearSubstitute();
        _factory.InteractionsQueryService
            .GetAsync(Arg.Any<InteractionsQueryFilters>(), Arg.Any<CancellationToken>())
            .Returns(EmptyGraph());

        // neighbours=5 is out of the documented 0/1/2 range; we clamp
        // to 2 rather than 400 so a typo in a URL doesn't blow up the
        // observation pane.
        var response = await _client.GetAsync("/api/v1/tenant/observation/interactions?neighbours=5", ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        await _factory.InteractionsQueryService.Received(1).GetAsync(
            Arg.Is<InteractionsQueryFilters>(f => f.Neighbours == 2),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetInteractions_BucketDay_ForwardsBucketKind()
    {
        var ct = TestContext.Current.CancellationToken;
        _factory.InteractionsQueryService.ClearSubstitute();
        _factory.InteractionsQueryService
            .GetAsync(Arg.Any<InteractionsQueryFilters>(), Arg.Any<CancellationToken>())
            .Returns(EmptyGraph());

        var response = await _client.GetAsync("/api/v1/tenant/observation/interactions?bucket=day", ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        await _factory.InteractionsQueryService.Received(1).GetAsync(
            Arg.Is<InteractionsQueryFilters>(f => f.Bucket == InteractionsBucket.Day),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetInteractions_UnitAsBareGuid_ParsedIntoFilter()
    {
        var ct = TestContext.Current.CancellationToken;
        var unitId = Guid.NewGuid();
        _factory.InteractionsQueryService.ClearSubstitute();
        _factory.InteractionsQueryService
            .GetAsync(Arg.Any<InteractionsQueryFilters>(), Arg.Any<CancellationToken>())
            .Returns(EmptyGraph());

        var response = await _client.GetAsync(
            $"/api/v1/tenant/observation/interactions?unit={unitId:N}", ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        await _factory.InteractionsQueryService.Received(1).GetAsync(
            Arg.Is<InteractionsQueryFilters>(f => f.Unit == unitId),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetInteractions_UnitAsCanonicalAddress_ParsedIntoFilter()
    {
        var ct = TestContext.Current.CancellationToken;
        var unitId = Guid.NewGuid();
        _factory.InteractionsQueryService.ClearSubstitute();
        _factory.InteractionsQueryService
            .GetAsync(Arg.Any<InteractionsQueryFilters>(), Arg.Any<CancellationToken>())
            .Returns(EmptyGraph());

        var address = $"unit:{GuidFormatter.Format(unitId)}";
        var response = await _client.GetAsync(
            $"/api/v1/tenant/observation/interactions?unit={Uri.EscapeDataString(address)}", ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        await _factory.InteractionsQueryService.Received(1).GetAsync(
            Arg.Is<InteractionsQueryFilters>(f => f.Unit == unitId),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetInteractions_SinceAndUntilProvided_OverrideDefaults()
    {
        var ct = TestContext.Current.CancellationToken;
        var since = new DateTimeOffset(2026, 5, 1, 0, 0, 0, TimeSpan.Zero);
        var until = new DateTimeOffset(2026, 5, 27, 0, 0, 0, TimeSpan.Zero);
        _factory.InteractionsQueryService.ClearSubstitute();
        _factory.InteractionsQueryService
            .GetAsync(Arg.Any<InteractionsQueryFilters>(), Arg.Any<CancellationToken>())
            .Returns(EmptyGraph());

        var response = await _client.GetAsync(
            $"/api/v1/tenant/observation/interactions?since={Uri.EscapeDataString(since.ToString("O"))}&until={Uri.EscapeDataString(until.ToString("O"))}",
            ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        await _factory.InteractionsQueryService.Received(1).GetAsync(
            Arg.Is<InteractionsQueryFilters>(f =>
                f.Since == since && f.Until == until),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetInteractions_ResponseShape_MirrorsServiceGraph()
    {
        var ct = TestContext.Current.CancellationToken;

        var fromId = Guid.NewGuid();
        var toId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

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

        _factory.InteractionsQueryService.ClearSubstitute();
        _factory.InteractionsQueryService
            .GetAsync(Arg.Any<InteractionsQueryFilters>(), Arg.Any<CancellationToken>())
            .Returns(graph);

        var response = await _client.GetAsync("/api/v1/tenant/observation/interactions", ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<InteractionsGraphResponse>(ct);
        body.ShouldNotBeNull();
        body!.Nodes.Count.ShouldBe(2);
        body.Nodes.Single(n => n.Id == GuidFormatter.Format(fromId)).DisplayName.ShouldBe("Ada");
        body.Edges.Count.ShouldBe(1);
        body.Edges[0].FromId.ShouldBe(GuidFormatter.Format(fromId));
        body.Edges[0].ToId.ShouldBe(GuidFormatter.Format(toId));
        body.Edges[0].Channels.ShouldBe(new[] { "unit" });
        body.Timeline.Count.ShouldBe(1);
        body.Truncated.ShouldBeNull();
    }

    [Fact]
    public async Task GetInteractions_TruncationPayload_RoundTripsWhenSet()
    {
        var ct = TestContext.Current.CancellationToken;
        var graph = new InteractionsGraph(
            Nodes: Array.Empty<InteractionsNode>(),
            Edges: Array.Empty<InteractionsEdge>(),
            Timeline: Array.Empty<InteractionsTimelineBucket>(),
            Truncated: new InteractionsTruncation(Total: 200, Kept: 50));

        _factory.InteractionsQueryService.ClearSubstitute();
        _factory.InteractionsQueryService
            .GetAsync(Arg.Any<InteractionsQueryFilters>(), Arg.Any<CancellationToken>())
            .Returns(graph);

        var response = await _client.GetAsync("/api/v1/tenant/observation/interactions", ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<InteractionsGraphResponse>(ct);
        body.ShouldNotBeNull();
        body!.Truncated.ShouldNotBeNull();
        body.Truncated!.Total.ShouldBe(200);
        body.Truncated.Kept.ShouldBe(50);
    }

    [Fact]
    public async Task GetInteractions_BadBucket_FallsBackToHour()
    {
        // Unknown bucket → falls back to hour (no 400). SSE clients
        // don't have a recoverable error path; the same forgiving
        // semantics apply to the snapshot endpoint so the two routes
        // share one vocabulary.
        var ct = TestContext.Current.CancellationToken;
        _factory.InteractionsQueryService.ClearSubstitute();
        _factory.InteractionsQueryService
            .GetAsync(Arg.Any<InteractionsQueryFilters>(), Arg.Any<CancellationToken>())
            .Returns(EmptyGraph());

        var response = await _client.GetAsync(
            "/api/v1/tenant/observation/interactions?bucket=fortnight", ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        await _factory.InteractionsQueryService.Received(1).GetAsync(
            Arg.Is<InteractionsQueryFilters>(f => f.Bucket == InteractionsBucket.Hour),
            Arg.Any<CancellationToken>());
    }

    private static InteractionsGraph EmptyGraph() => new(
        Array.Empty<InteractionsNode>(),
        Array.Empty<InteractionsEdge>(),
        Array.Empty<InteractionsTimelineBucket>(),
        Truncated: null);

    // ---- History endpoint (#2872) -----------------------------------------
    // The /history surface is the rewind affordance for the Interactions
    // view. Tests focus on the HTTP plumbing — query-string binding,
    // defaults, cap/maxPulses parsing, truncation envelope shape — and
    // mock the query service. Aggregation correctness lives in
    // InteractionsQueryServiceTests.

    [Fact]
    public async Task GetInteractionsHistory_NoParams_AppliesDefaultsServerSide()
    {
        var ct = TestContext.Current.CancellationToken;
        _factory.InteractionsQueryService.ClearSubstitute();
        _factory.InteractionsQueryService
            .GetHistoryAsync(Arg.Any<InteractionsHistoryFilters>(), Arg.Any<CancellationToken>())
            .Returns(EmptyHistory());

        var response = await _client.GetAsync(
            "/api/v1/tenant/observation/interactions/history", ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        await _factory.InteractionsQueryService.Received(1).GetHistoryAsync(
            Arg.Is<InteractionsHistoryFilters>(f =>
                f.Cap == 50 &&
                f.Neighbours == 2 &&
                f.MaxPulses == 5000 &&
                f.Unit == null &&
                f.Participant == null &&
                Math.Abs((f.Until - f.Since).TotalMinutes - 10) < 1.0),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetInteractionsHistory_MaxPulsesProvided_ForwardsValue()
    {
        var ct = TestContext.Current.CancellationToken;
        _factory.InteractionsQueryService.ClearSubstitute();
        _factory.InteractionsQueryService
            .GetHistoryAsync(Arg.Any<InteractionsHistoryFilters>(), Arg.Any<CancellationToken>())
            .Returns(EmptyHistory());

        var response = await _client.GetAsync(
            "/api/v1/tenant/observation/interactions/history?maxPulses=5", ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        await _factory.InteractionsQueryService.Received(1).GetHistoryAsync(
            Arg.Is<InteractionsHistoryFilters>(f => f.MaxPulses == 5),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetInteractionsHistory_MaxPulsesZeroOrNegative_FallsBackToDefault()
    {
        // `maxPulses=0` would silently disable truncation if forwarded —
        // surfaces neither a 400 nor an infinite-result hazard; we
        // explicitly fall back to the default to keep the contract
        // forgiving in the spirit of the snapshot endpoint.
        var ct = TestContext.Current.CancellationToken;
        _factory.InteractionsQueryService.ClearSubstitute();
        _factory.InteractionsQueryService
            .GetHistoryAsync(Arg.Any<InteractionsHistoryFilters>(), Arg.Any<CancellationToken>())
            .Returns(EmptyHistory());

        var response = await _client.GetAsync(
            "/api/v1/tenant/observation/interactions/history?maxPulses=0", ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        await _factory.InteractionsQueryService.Received(1).GetHistoryAsync(
            Arg.Is<InteractionsHistoryFilters>(f => f.MaxPulses == 5000),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetInteractionsHistory_NeighboursOutOfRange_ClampedToMax()
    {
        var ct = TestContext.Current.CancellationToken;
        _factory.InteractionsQueryService.ClearSubstitute();
        _factory.InteractionsQueryService
            .GetHistoryAsync(Arg.Any<InteractionsHistoryFilters>(), Arg.Any<CancellationToken>())
            .Returns(EmptyHistory());

        var response = await _client.GetAsync(
            "/api/v1/tenant/observation/interactions/history?neighbours=5", ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        await _factory.InteractionsQueryService.Received(1).GetHistoryAsync(
            Arg.Is<InteractionsHistoryFilters>(f => f.Neighbours == 2),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetInteractionsHistory_CapNone_DisablesNodeTruncation()
    {
        var ct = TestContext.Current.CancellationToken;
        _factory.InteractionsQueryService.ClearSubstitute();
        _factory.InteractionsQueryService
            .GetHistoryAsync(Arg.Any<InteractionsHistoryFilters>(), Arg.Any<CancellationToken>())
            .Returns(EmptyHistory());

        var response = await _client.GetAsync(
            "/api/v1/tenant/observation/interactions/history?cap=none", ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        await _factory.InteractionsQueryService.Received(1).GetHistoryAsync(
            Arg.Is<InteractionsHistoryFilters>(f => f.Cap == null),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetInteractionsHistory_CapNonInteger_FallsBackToDefault()
    {
        var ct = TestContext.Current.CancellationToken;
        _factory.InteractionsQueryService.ClearSubstitute();
        _factory.InteractionsQueryService
            .GetHistoryAsync(Arg.Any<InteractionsHistoryFilters>(), Arg.Any<CancellationToken>())
            .Returns(EmptyHistory());

        var response = await _client.GetAsync(
            "/api/v1/tenant/observation/interactions/history?cap=banana", ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        await _factory.InteractionsQueryService.Received(1).GetHistoryAsync(
            Arg.Is<InteractionsHistoryFilters>(f => f.Cap == 50),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetInteractionsHistory_ResponseShape_MirrorsServiceHistory()
    {
        var ct = TestContext.Current.CancellationToken;

        var fromId = Guid.NewGuid();
        var toId = Guid.NewGuid();
        var messageId = Guid.NewGuid();
        var threadId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        var history = new InteractionsHistory(
            Nodes: new List<InteractionsNode>
            {
                new(GuidFormatter.Format(fromId), "agent", "Ada", Sent: 1, Received: 0),
                new(GuidFormatter.Format(toId), "unit", "Engineering", Sent: 0, Received: 1),
            },
            Edges: new List<InteractionsEdge>
            {
                new(GuidFormatter.Format(fromId), GuidFormatter.Format(toId),
                    Count: 1, FirstAt: now, LastAt: now,
                    Channels: new[] { "unit" }),
            },
            Pulses: new List<InteractionsPulse>
            {
                new(GuidFormatter.Format(messageId),
                    GuidFormatter.Format(fromId),
                    GuidFormatter.Format(toId),
                    now,
                    GuidFormatter.Format(threadId),
                    "unit"),
            },
            Truncated: null);

        _factory.InteractionsQueryService.ClearSubstitute();
        _factory.InteractionsQueryService
            .GetHistoryAsync(Arg.Any<InteractionsHistoryFilters>(), Arg.Any<CancellationToken>())
            .Returns(history);

        var response = await _client.GetAsync(
            "/api/v1/tenant/observation/interactions/history", ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<InteractionsHistoryResponse>(ct);
        body.ShouldNotBeNull();
        body!.Nodes.Count.ShouldBe(2);
        body.Edges.Count.ShouldBe(1);
        body.Pulses.Count.ShouldBe(1);
        body.Pulses[0].MessageId.ShouldBe(GuidFormatter.Format(messageId));
        body.Pulses[0].FromId.ShouldBe(GuidFormatter.Format(fromId));
        body.Pulses[0].ToId.ShouldBe(GuidFormatter.Format(toId));
        body.Pulses[0].ThreadId.ShouldBe(GuidFormatter.Format(threadId));
        body.Pulses[0].Channel.ShouldBe("unit");
        body.Truncated.ShouldBeNull();
    }

    [Fact]
    public async Task GetInteractionsHistory_TruncationEnvelope_BothBranchesRoundTrip()
    {
        // Service returns both node-level and pulse-level truncation; the
        // host DTO must round-trip the nested pulses block.
        var ct = TestContext.Current.CancellationToken;

        var history = new InteractionsHistory(
            Nodes: Array.Empty<InteractionsNode>(),
            Edges: Array.Empty<InteractionsEdge>(),
            Pulses: Array.Empty<InteractionsPulse>(),
            Truncated: new InteractionsHistoryTruncation(
                Total: 200, Kept: 50,
                Pulses: new InteractionsPulseTruncation(Total: 12000, Kept: 5000)));

        _factory.InteractionsQueryService.ClearSubstitute();
        _factory.InteractionsQueryService
            .GetHistoryAsync(Arg.Any<InteractionsHistoryFilters>(), Arg.Any<CancellationToken>())
            .Returns(history);

        var response = await _client.GetAsync(
            "/api/v1/tenant/observation/interactions/history", ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<InteractionsHistoryResponse>(ct);
        body.ShouldNotBeNull();
        body!.Truncated.ShouldNotBeNull();
        body.Truncated!.Total.ShouldBe(200);
        body.Truncated.Kept.ShouldBe(50);
        body.Truncated.Pulses.ShouldNotBeNull();
        body.Truncated.Pulses!.Total.ShouldBe(12000);
        body.Truncated.Pulses.Kept.ShouldBe(5000);
    }

    [Fact]
    public async Task GetInteractionsHistory_TruncationEnvelope_PulseBranchOnly()
    {
        // Only pulse truncation fired; node total == kept.
        var ct = TestContext.Current.CancellationToken;

        var history = new InteractionsHistory(
            Nodes: Array.Empty<InteractionsNode>(),
            Edges: Array.Empty<InteractionsEdge>(),
            Pulses: Array.Empty<InteractionsPulse>(),
            Truncated: new InteractionsHistoryTruncation(
                Total: 7, Kept: 7,
                Pulses: new InteractionsPulseTruncation(Total: 100, Kept: 50)));

        _factory.InteractionsQueryService.ClearSubstitute();
        _factory.InteractionsQueryService
            .GetHistoryAsync(Arg.Any<InteractionsHistoryFilters>(), Arg.Any<CancellationToken>())
            .Returns(history);

        var response = await _client.GetAsync(
            "/api/v1/tenant/observation/interactions/history", ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<InteractionsHistoryResponse>(ct);
        body.ShouldNotBeNull();
        body!.Truncated.ShouldNotBeNull();
        body.Truncated!.Total.ShouldBe(7);
        body.Truncated.Kept.ShouldBe(7);
        body.Truncated.Pulses.ShouldNotBeNull();
        body.Truncated.Pulses!.Kept.ShouldBe(50);
    }

    [Fact]
    public async Task GetInteractionsHistory_WithoutTenantObserverRole_Returns403()
    {
        // Spin up a separate factory whose IRoleClaimSource emits TenantUser
        // (no TenantObserver). The endpoint's RequireAuthorization gate
        // must reject. This is the only assertion on the policy gate
        // because the gating itself is exercised at the IAuthorizationService
        // level in RolePoliciesTests; here we verify the endpoint chains
        // through it.
        var ct = TestContext.Current.CancellationToken;
        using var nonObserverFactory = BuildFactoryWithoutTenantObserver();
        using var client = nonObserverFactory.CreateClient();

        var response = await client.GetAsync(
            "/api/v1/tenant/observation/interactions/history", ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    private static InteractionsHistory EmptyHistory() => new(
        Array.Empty<InteractionsNode>(),
        Array.Empty<InteractionsEdge>(),
        Array.Empty<InteractionsPulse>(),
        Truncated: null);

    /// <summary>
    /// Spins up a separate factory with an <see cref="Cvoya.Spring.Host.Api.Auth.IRoleClaimSource"/>
    /// stub that omits the <see cref="Cvoya.Spring.Core.Security.PlatformRoles.TenantObserver"/>
    /// claim. The OSS default grants every role; this factory exercises
    /// the 403 branch for the /history endpoint without touching the
    /// shared singleton factory used by the other tests in this class.
    /// </summary>
    private static Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> BuildFactoryWithoutTenantObserver()
    {
        return new CustomWebApplicationFactory()
            .WithWebHostBuilder(builder =>
            {
                builder.ConfigureServices(services =>
                {
                    var existing = services
                        .Where(d => d.ServiceType == typeof(Cvoya.Spring.Host.Api.Auth.IRoleClaimSource))
                        .ToList();
                    foreach (var d in existing)
                    {
                        services.Remove(d);
                    }
                    services.AddSingleton<Cvoya.Spring.Host.Api.Auth.IRoleClaimSource, NonObserverClaimSource>();
                });
            });
    }

    private sealed class NonObserverClaimSource : Cvoya.Spring.Host.Api.Auth.IRoleClaimSource
    {
        public IEnumerable<System.Security.Claims.Claim> GetRoleClaims(System.Security.Claims.ClaimsIdentity identity)
        {
            // Grant TenantUser only — TenantObserver gates /interactions.
            yield return new System.Security.Claims.Claim(
                System.Security.Claims.ClaimTypes.Role,
                Cvoya.Spring.Core.Security.PlatformRoles.TenantUser);
        }
    }

    // ---- SSE stream tests -------------------------------------------------
    // The stream subscribes to IActivityEventBus.ActivityStream, filters for
    // MessageArrived events whose Details JSON carries the (from, messageId)
    // tuple that MessageArrivedDetails.Build writes, and coalesces pulses
    // on a periodic 250 ms timer. The tests below replace the bus with a
    // hot Subject<ActivityEvent> via the existing CustomWebApplicationFactory
    // wiring, push synthetic events, and read the SSE response.

    [Fact]
    public async Task StreamInteractions_Headers_ServedAsTextEventStream()
    {
        var ct = TestContext.Current.CancellationToken;

        using var subject = new Subject<ActivityEvent>();
        _factory.ActivityEventBus.ActivityStream.Returns(subject);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(3));

        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/tenant/observation/interactions/stream");
        try
        {
            using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            response.StatusCode.ShouldBe(HttpStatusCode.OK);
            response.Content.Headers.ContentType?.MediaType.ShouldBe("text/event-stream");
        }
        catch (OperationCanceledException)
        {
            // Expected — we cancel after reading headers; SSE never completes.
        }
    }

    [Fact]
    public async Task StreamInteractions_OnFirstMessage_EmitsNodeAddedThenEdgeAddedThenPulse()
    {
        // Push one MessageArrived event for a previously-unseen (from, to)
        // pair. The relay must emit node-added (both endpoints) and
        // edge-added before the first pulse — the visualization needs the
        // node/edge geometry allocated before the animation that touches
        // them.
        var ct = TestContext.Current.CancellationToken;

        using var subject = new Subject<ActivityEvent>();
        _factory.ActivityEventBus.ActivityStream.Returns(subject);

        var fromId = Guid.NewGuid();
        var toId = Guid.NewGuid();
        var messageId = Guid.NewGuid();

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(15));

        using var request = new HttpRequestMessage(
            HttpMethod.Get, "/api/v1/tenant/observation/interactions/stream?coalesceMs=100");
        using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        await using var stream = await response.Content.ReadAsStreamAsync(cts.Token);
        using var reader = new StreamReader(stream);

        // The subscription is hot by the time headers flush; emit one
        // event and read the resulting SSE frames. We need at least 4:
        // 2 node-added (one per endpoint), 1 edge-added, and 1 pulse
        // emitted by the coalesce timer.
        subject.OnNext(BuildMessageArrivedEvent(fromId, "agent", toId, "unit", messageId));

        var frames = await ReadFramesAsync(reader, expected: 4, cts.Token);

        frames.Count.ShouldBeGreaterThanOrEqualTo(4);
        // node-added arrives before the pulse for both ends.
        var pulseIdx = frames.FindIndex(f => f.EventName == InteractionsStreamEvents.Pulse);
        var nodeIndices = frames
            .Select((f, i) => (f, i))
            .Where(t => t.f.EventName == InteractionsStreamEvents.NodeAdded)
            .Select(t => t.i)
            .ToList();
        var edgeIndices = frames
            .Select((f, i) => (f, i))
            .Where(t => t.f.EventName == InteractionsStreamEvents.EdgeAdded)
            .Select(t => t.i)
            .ToList();

        pulseIdx.ShouldBeGreaterThan(-1, "expected a pulse frame");
        nodeIndices.Count.ShouldBe(2, "expected exactly two node-added frames");
        edgeIndices.Count.ShouldBe(1, "expected exactly one edge-added frame");
        nodeIndices.ShouldAllBe(i => i < pulseIdx, "node-added must precede pulse");
        edgeIndices.ShouldAllBe(i => i < pulseIdx, "edge-added must precede pulse");

        // Pulse payload references the right ids and the one message id.
        var pulse = JsonSerializer.Deserialize<InteractionsPulseFrame>(
            frames[pulseIdx].DataJson, JsonOpts);
        pulse.ShouldNotBeNull();
        pulse!.FromId.ShouldBe(GuidFormatter.Format(fromId));
        pulse.ToId.ShouldBe(GuidFormatter.Format(toId));
        pulse.MessageIds.Count.ShouldBe(1);
        pulse.MessageIds[0].ShouldBe(GuidFormatter.Format(messageId));
        pulse.Count.ShouldBe(1);
        pulse.Channel.ShouldBe("unit");
    }

    [Fact]
    public async Task StreamInteractions_BurstOnSameEdge_CoalescedIntoOnePulse()
    {
        // Push 5 messages for the same (from, to) pair within one
        // coalesce window. The relay must emit one pulse with count = 5
        // and every message id present, rather than five separate
        // pulses.
        var ct = TestContext.Current.CancellationToken;

        using var subject = new Subject<ActivityEvent>();
        _factory.ActivityEventBus.ActivityStream.Returns(subject);

        var fromId = Guid.NewGuid();
        var toId = Guid.NewGuid();
        var messageIds = Enumerable.Range(0, 5).Select(_ => Guid.NewGuid()).ToList();

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(15));

        // coalesceMs=300 — wide enough that all 5 events land in the
        // same window before the first flush tick.
        using var request = new HttpRequestMessage(
            HttpMethod.Get, "/api/v1/tenant/observation/interactions/stream?coalesceMs=300");
        using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        await using var stream = await response.Content.ReadAsStreamAsync(cts.Token);
        using var reader = new StreamReader(stream);

        foreach (var mid in messageIds)
        {
            subject.OnNext(BuildMessageArrivedEvent(fromId, "agent", toId, "agent", mid));
        }

        var frames = await ReadFramesAsync(reader, expected: 4, cts.Token);

        var pulses = frames.Where(f => f.EventName == InteractionsStreamEvents.Pulse).ToList();
        pulses.Count.ShouldBe(1, "burst within one coalesce window must produce one pulse");
        var pulse = JsonSerializer.Deserialize<InteractionsPulseFrame>(pulses[0].DataJson, JsonOpts);
        pulse.ShouldNotBeNull();
        pulse!.Count.ShouldBe(5);
        pulse.MessageIds.Count.ShouldBe(5);
        pulse.MessageIds.ShouldBe(messageIds.Select(g => GuidFormatter.Format(g)).ToArray(), ignoreOrder: true);
    }

    [Fact]
    public async Task StreamInteractions_RateCapExceeded_EmitsThrottledFrame()
    {
        // maxRate=2 with 10 events in the same second → 8 events dropped.
        // The throttled frame emits on the next flush tick with the
        // accumulated drop count.
        var ct = TestContext.Current.CancellationToken;

        using var subject = new Subject<ActivityEvent>();
        _factory.ActivityEventBus.ActivityStream.Returns(subject);

        var fromId = Guid.NewGuid();
        var toId = Guid.NewGuid();

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(15));

        using var request = new HttpRequestMessage(
            HttpMethod.Get, "/api/v1/tenant/observation/interactions/stream?coalesceMs=200&maxRate=2");
        using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        await using var stream = await response.Content.ReadAsStreamAsync(cts.Token);
        using var reader = new StreamReader(stream);

        // Push 10 events with the same wall-clock second; only the
        // first 2 should pass the rate cap.
        var ts = DateTimeOffset.UtcNow;
        for (var i = 0; i < 10; i++)
        {
            subject.OnNext(BuildMessageArrivedEvent(fromId, "agent", toId, "agent", Guid.NewGuid(), ts));
        }

        var frames = await ReadFramesAsync(reader, expected: 5, cts.Token);

        var throttled = frames.Where(f => f.EventName == InteractionsStreamEvents.Throttled).ToList();
        throttled.Count.ShouldBeGreaterThan(0, "expected at least one throttled frame");
        var t = JsonSerializer.Deserialize<InteractionsThrottledFrame>(throttled[0].DataJson, JsonOpts);
        t.ShouldNotBeNull();
        t!.Dropped.ShouldBeGreaterThanOrEqualTo(8);
    }

    [Fact]
    public async Task StreamInteractions_FramesCarryMonotonicEventId()
    {
        // Each frame carries an `id:` line for Last-Event-ID resume.
        // The counter is strictly increasing — successive frames have
        // strictly-greater ids regardless of frame type.
        var ct = TestContext.Current.CancellationToken;

        using var subject = new Subject<ActivityEvent>();
        _factory.ActivityEventBus.ActivityStream.Returns(subject);

        var fromId = Guid.NewGuid();
        var toId = Guid.NewGuid();

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(15));

        using var request = new HttpRequestMessage(
            HttpMethod.Get, "/api/v1/tenant/observation/interactions/stream?coalesceMs=100");
        using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        await using var stream = await response.Content.ReadAsStreamAsync(cts.Token);
        using var reader = new StreamReader(stream);

        subject.OnNext(BuildMessageArrivedEvent(fromId, "agent", toId, "agent", Guid.NewGuid()));

        var frames = await ReadFramesAsync(reader, expected: 3, cts.Token);

        frames.Count.ShouldBeGreaterThanOrEqualTo(2);
        for (var i = 1; i < frames.Count; i++)
        {
            frames[i].Id.ShouldBeGreaterThan(frames[i - 1].Id, "event ids must be strictly increasing");
        }
    }

    [Fact]
    public async Task StreamInteractions_LastEventIdHeader_SkipsFramesAtOrBelowResumePoint()
    {
        // Set Last-Event-ID to a value that will cause the relay to
        // skip the first N frames (everything before id > supplied
        // value). We pick a high enough resume point that the first
        // pulse-and-prelude burst is fully suppressed.
        var ct = TestContext.Current.CancellationToken;

        using var subject = new Subject<ActivityEvent>();
        _factory.ActivityEventBus.ActivityStream.Returns(subject);

        var fromA = Guid.NewGuid();
        var toA = Guid.NewGuid();
        var fromB = Guid.NewGuid();
        var toB = Guid.NewGuid();

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(15));

        // Set Last-Event-ID = 100 — the relay starts new ids from 101,
        // so every frame passes the resume check.
        using var request = new HttpRequestMessage(
            HttpMethod.Get, "/api/v1/tenant/observation/interactions/stream?coalesceMs=100");
        request.Headers.TryAddWithoutValidation("Last-Event-ID", "100");
        using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        await using var stream = await response.Content.ReadAsStreamAsync(cts.Token);
        using var reader = new StreamReader(stream);

        subject.OnNext(BuildMessageArrivedEvent(fromA, "agent", toA, "agent", Guid.NewGuid()));

        var frames = await ReadFramesAsync(reader, expected: 3, cts.Token);

        // The relay's id counter started at 100; every frame emitted
        // has id > 100. The resume header therefore suppresses
        // nothing — all frames make it through.
        frames.Count.ShouldBeGreaterThanOrEqualTo(3);
        frames.ShouldAllBe(f => f.Id > 100);
    }

    [Fact]
    public async Task StreamInteractions_ConnectorRecipient_FilteredOut()
    {
        // Per ADR-0048 a connector address is provenance-only — the
        // relay must drop any MessageArrived event whose recipient is
        // a connector (the source of the event).
        var ct = TestContext.Current.CancellationToken;

        using var subject = new Subject<ActivityEvent>();
        _factory.ActivityEventBus.ActivityStream.Returns(subject);

        var fromId = Guid.NewGuid();
        var connectorId = Guid.NewGuid();
        var agentFromId = Guid.NewGuid();
        var agentToId = Guid.NewGuid();

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(15));

        using var request = new HttpRequestMessage(
            HttpMethod.Get, "/api/v1/tenant/observation/interactions/stream?coalesceMs=100");
        using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        await using var stream = await response.Content.ReadAsStreamAsync(cts.Token);
        using var reader = new StreamReader(stream);

        // Synthetic "agent → connector" arrival — illegal per ADR-0048
        // but defensible relay should still drop it.
        subject.OnNext(BuildMessageArrivedEvent(fromId, "agent", connectorId, "connector", Guid.NewGuid()));
        // Routine agent → agent traffic — should pulse normally.
        subject.OnNext(BuildMessageArrivedEvent(agentFromId, "agent", agentToId, "agent", Guid.NewGuid()));

        var frames = await ReadFramesAsync(reader, expected: 3, cts.Token);

        // Inspect every node-added frame; none should reference the
        // connector id. The agent → agent edge should be present.
        var nodes = frames
            .Where(f => f.EventName == InteractionsStreamEvents.NodeAdded)
            .Select(f => JsonSerializer.Deserialize<InteractionsNodeAddedFrame>(f.DataJson, JsonOpts)!)
            .ToList();
        nodes.ShouldNotContain(n => n.Id == GuidFormatter.Format(connectorId));
        nodes.ShouldContain(n => n.Id == GuidFormatter.Format(agentFromId));
        nodes.ShouldContain(n => n.Id == GuidFormatter.Format(agentToId));
    }

    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Reads the SSE stream until <paramref name="expected"/> frames have
    /// been parsed or cancellation trips. Each frame is (id, event, data
    /// JSON) — the relay's wire shape on the network is one
    /// <c>id: N\nevent: name\ndata: {...}\n\n</c> block per frame.
    /// </summary>
    private static async Task<List<ParsedFrame>> ReadFramesAsync(
        StreamReader reader, int expected, CancellationToken ct)
    {
        var frames = new List<ParsedFrame>();
        long? currentId = null;
        string? currentEvent = null;
        string? currentData = null;

        while (frames.Count < expected)
        {
            using var lineCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            lineCts.CancelAfter(TimeSpan.FromSeconds(5));

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
                break;
            }

            if (line is null) break;

            if (line.StartsWith("id: ", StringComparison.Ordinal))
            {
                currentId = long.Parse(line[4..]);
            }
            else if (line.StartsWith("event: ", StringComparison.Ordinal))
            {
                currentEvent = line[7..];
            }
            else if (line.StartsWith("data: ", StringComparison.Ordinal))
            {
                currentData = line[6..];
            }
            else if (string.IsNullOrEmpty(line))
            {
                if (currentId is { } id && currentEvent is { } evt && currentData is { } data)
                {
                    frames.Add(new ParsedFrame(id, evt, data));
                }
                currentId = null;
                currentEvent = null;
                currentData = null;
            }
        }

        return frames;
    }

    private record ParsedFrame(long Id, string EventName, string DataJson);

    /// <summary>
    /// Builds a synthetic <see cref="ActivityEvent"/> of type
    /// <see cref="ActivityEventType.MessageArrived"/> with the Details
    /// payload <see cref="Cvoya.Spring.Dapr.Actors.MessageArrivedDetails"/>
    /// would write — recipient as <c>Source</c>, sender encoded as
    /// <c>"from": "scheme://hex"</c>, plus the message id.
    /// </summary>
    private static ActivityEvent BuildMessageArrivedEvent(
        Guid fromId, string fromScheme,
        Guid toId, string toScheme,
        Guid messageId,
        DateTimeOffset? timestamp = null)
    {
        var details = JsonSerializer.SerializeToElement(new Dictionary<string, object?>
        {
            ["messageId"] = messageId.ToString("D"),
            ["from"] = $"{fromScheme}://{GuidFormatter.Format(fromId)}",
            ["to"] = $"{toScheme}://{GuidFormatter.Format(toId)}",
            ["messageType"] = "Domain",
        });

        return new ActivityEvent(
            Id: Guid.NewGuid(),
            Timestamp: timestamp ?? DateTimeOffset.UtcNow,
            Source: new Address(toScheme, toId),
            EventType: ActivityEventType.MessageArrived,
            Severity: ActivitySeverity.Info,
            Summary: "msg",
            Details: details,
            CorrelationId: Guid.NewGuid().ToString("D"));
    }

    /// <summary>
    /// Factory specialisation that wires an <see cref="IInteractionsQueryService"/>
    /// mock through the DI container. Mirrors the
    /// <see cref="ObservationEndpointsTests.Factory"/> pattern so this test
    /// class is hermetic from the rest of the suite.
    /// </summary>
    public sealed class Factory : CustomWebApplicationFactory
    {
        public IInteractionsQueryService InteractionsQueryService { get; } =
            Substitute.For<IInteractionsQueryService>();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureServices(services =>
            {
                var descriptors = services
                    .Where(d => d.ServiceType == typeof(IInteractionsQueryService))
                    .ToList();
                foreach (var d in descriptors)
                {
                    services.Remove(d);
                }

                services.AddSingleton(InteractionsQueryService);
            });
        }
    }
}
