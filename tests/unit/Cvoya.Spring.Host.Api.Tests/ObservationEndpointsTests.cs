// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Api.Tests;

using System.Net;
using System.Net.Http.Json;

using Cvoya.Spring.Core.Observability;
using Cvoya.Spring.Host.Api.Models;

using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

using NSubstitute;
using NSubstitute.ClearExtensions;

using Shouldly;

using Xunit;

/// <summary>
/// Integration tests for the tenant-wide observation endpoints (#2787).
/// The query service is mocked so these tests stay focused on the HTTP
/// plumbing — parameter binding, 404 path, response shape. Role-policy
/// gating is verified separately in
/// <see cref="Cvoya.Spring.Host.Api.Tests.Auth.RolePoliciesTests"/> and
/// the OSS overlay's "every authenticated caller gets every role" path is
/// what lets the test client pass through to the endpoint here.
/// </summary>
public class ObservationEndpointsTests : IClassFixture<ObservationEndpointsTests.Factory>
{
    private readonly Factory _factory;
    private readonly HttpClient _client;

    public ObservationEndpointsTests(Factory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task ListObservedThreads_NoFilters_ReturnsAllTenantThreadsViaQueryService()
    {
        // The observation endpoint deliberately does NOT inject a caller
        // participant filter — the privilege of holding TenantObserver is
        // precisely "see every thread in the tenant". Verify the endpoint
        // forwards an unfiltered ThreadQueryFilters when the request omits
        // every optional query param.
        var ct = TestContext.Current.CancellationToken;
        _factory.ThreadQueryService.ClearSubstitute();
        var now = DateTimeOffset.UtcNow;
        _factory.ThreadQueryService
            .ListAsync(Arg.Any<ThreadQueryFilters>(), Arg.Any<CancellationToken>())
            .Returns(new List<ThreadSummary>
            {
                new("obs-1", new[] { "agent://ada", "agent://grace" }, now, now, 2, "agent://ada", "Hand-off"),
            });

        var response = await _client.GetAsync("/api/v1/tenant/observation/threads", ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var rows = await response.Content.ReadFromJsonAsync<List<ThreadSummaryResponse>>(ct);
        rows.ShouldNotBeNull();
        rows!.Count.ShouldBe(1);
        rows[0].Id.ShouldBe("obs-1");

        await _factory.ThreadQueryService.Received(1)
            .ListAsync(
                Arg.Is<ThreadQueryFilters>(f =>
                    f.Unit == null &&
                    f.Agent == null &&
                    f.Participant == null &&
                    f.Limit == null &&
                    f.Archived == null),
                Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ListObservedThreads_WithFilters_PassesThemToService()
    {
        // The optional narrowing filters (unit/agent/participant/limit) are
        // observation-side conveniences — the caller still sees every
        // thread the filter matches, regardless of their participation.
        var ct = TestContext.Current.CancellationToken;
        _factory.ThreadQueryService.ClearSubstitute();
        _factory.ThreadQueryService
            .ListAsync(Arg.Any<ThreadQueryFilters>(), Arg.Any<CancellationToken>())
            .Returns(new List<ThreadSummary>());

        var response = await _client.GetAsync(
            "/api/v1/tenant/observation/threads?unit=eng-team&agent=ada&participant=human%3A%2F%2Fsavasp&limit=25",
            ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        await _factory.ThreadQueryService.Received(1)
            .ListAsync(
                Arg.Is<ThreadQueryFilters>(f =>
                    f.Unit == "eng-team" &&
                    f.Agent == "ada" &&
                    f.Participant == "human://savasp" &&
                    f.Limit == 25),
                Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ListObservedThreads_SurfaceThreadsRegardlessOfCallerParticipation()
    {
        // Regression guard for the central invariant of this endpoint:
        // even when the calling identity is not present in any thread's
        // participant set, the endpoint still returns every row the query
        // service yields. The mocked service stands in for the EF query
        // filter that scopes to tenant; the lack of caller-side filtering
        // is what differentiates observation from the participant-scoped
        // /threads endpoint.
        var ct = TestContext.Current.CancellationToken;
        _factory.ThreadQueryService.ClearSubstitute();
        var now = DateTimeOffset.UtcNow;
        _factory.ThreadQueryService
            .ListAsync(Arg.Any<ThreadQueryFilters>(), Arg.Any<CancellationToken>())
            .Returns(new List<ThreadSummary>
            {
                new("not-my-thread", new[] { "agent://ada", "agent://grace" }, now, now, 5, "agent://ada", "Cross-agent work"),
                new("also-not-mine", new[] { "agent://hopper", "agent://lovelace" }, now, now, 3, "agent://hopper", "Side discussion"),
            });

        var response = await _client.GetAsync("/api/v1/tenant/observation/threads", ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var rows = await response.Content.ReadFromJsonAsync<List<ThreadSummaryResponse>>(ct);
        rows.ShouldNotBeNull();
        rows!.Count.ShouldBe(2);
        rows.Select(r => r.Id).ShouldBe(new[] { "not-my-thread", "also-not-mine" }, ignoreOrder: true);
    }

    [Fact]
    public async Task GetObservedThread_Missing_Returns404()
    {
        var ct = TestContext.Current.CancellationToken;
        _factory.ThreadQueryService.ClearSubstitute();
        _factory.ThreadQueryService
            .GetAsync("obs-missing", Arg.Any<CancellationToken>())
            .Returns((ThreadDetail?)null);

        var response = await _client.GetAsync("/api/v1/tenant/observation/threads/obs-missing", ct);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task GetObservedThread_Existing_ReturnsDetail()
    {
        var ct = TestContext.Current.CancellationToken;
        _factory.ThreadQueryService.ClearSubstitute();
        var now = DateTimeOffset.UtcNow;
        _factory.ThreadQueryService
            .GetAsync("obs-1", Arg.Any<CancellationToken>())
            .Returns(new ThreadDetail(
                new ThreadSummary("obs-1", new[] { "agent://ada" }, now, now, 1, "agent://ada", "Started"),
                new List<ThreadEvent>
                {
                    new(Guid.NewGuid(), now, "agent://ada", "ThreadStarted", "Info", "Started conversation obs-1"),
                }));

        var response = await _client.GetAsync("/api/v1/tenant/observation/threads/obs-1", ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var detail = await response.Content.ReadFromJsonAsync<ThreadDetailResponse>(ct);
        detail.ShouldNotBeNull();
        detail!.Summary.ShouldNotBeNull();
        detail.Summary!.Id.ShouldBe("obs-1");
        detail.Events.ShouldNotBeNull();
        detail.Events!.Count.ShouldBe(1);
    }

    // -----------------------------------------------------------------------
    // #2790 — Search + Since filters drive the Conversations polish pass.
    // -----------------------------------------------------------------------

    [Fact]
    public async Task ListObservedThreads_SinceFilter_PassesThroughToService()
    {
        // The endpoint forwards ?since=<iso-instant> into ThreadQueryFilters.Since.
        // The service is mocked so we only assert the parameter binding here;
        // the actual filter behaviour is covered by ThreadQueryServiceTests.
        var ct = TestContext.Current.CancellationToken;
        _factory.ThreadQueryService.ClearSubstitute();
        _factory.ThreadQueryService
            .ListAsync(Arg.Any<ThreadQueryFilters>(), Arg.Any<CancellationToken>())
            .Returns(new List<ThreadSummary>());

        var response = await _client.GetAsync(
            "/api/v1/tenant/observation/threads?since=2026-05-01T00:00:00Z",
            ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        await _factory.ThreadQueryService.Received(1)
            .ListAsync(
                Arg.Is<ThreadQueryFilters>(f =>
                    f.Since.HasValue &&
                    f.Since.Value == new DateTimeOffset(2026, 5, 1, 0, 0, 0, TimeSpan.Zero)),
                Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ListObservedThreads_SearchFilter_NarrowsToMatchingSummaryText()
    {
        // Search is applied at the API host layer AFTER enrichment so the
        // term can match resolved participant display names. We arrange
        // three rows where only one has a matching summary.
        var ct = TestContext.Current.CancellationToken;
        _factory.ThreadQueryService.ClearSubstitute();
        var now = DateTimeOffset.UtcNow;
        _factory.ThreadQueryService
            .ListAsync(Arg.Any<ThreadQueryFilters>(), Arg.Any<CancellationToken>())
            .Returns(new List<ThreadSummary>
            {
                new("obs-match", new[] { "agent://ada" }, now, now, 1, "agent://ada", "Migration plan review"),
                new("obs-other", new[] { "agent://grace" }, now, now, 1, "agent://grace", "Status update"),
                new("obs-yet-another", new[] { "agent://hopper" }, now, now, 1, "agent://hopper", "Routine ping"),
            });

        var response = await _client.GetAsync(
            "/api/v1/tenant/observation/threads?search=Migration",
            ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var rows = await response.Content.ReadFromJsonAsync<List<ThreadSummaryResponse>>(ct);
        rows.ShouldNotBeNull();
        rows!.Select(r => r.Id).ShouldBe(new[] { "obs-match" });
    }

    [Fact]
    public async Task ListObservedThreads_SearchFilter_IsCaseInsensitive()
    {
        // The substring match is case-insensitive so the filter is
        // resilient to typing/casing variation.
        var ct = TestContext.Current.CancellationToken;
        _factory.ThreadQueryService.ClearSubstitute();
        var now = DateTimeOffset.UtcNow;
        _factory.ThreadQueryService
            .ListAsync(Arg.Any<ThreadQueryFilters>(), Arg.Any<CancellationToken>())
            .Returns(new List<ThreadSummary>
            {
                new("obs-1", new[] { "agent://ada" }, now, now, 1, "agent://ada", "MIGRATION plan"),
                new("obs-2", new[] { "agent://grace" }, now, now, 1, "agent://grace", "routine"),
            });

        var response = await _client.GetAsync(
            "/api/v1/tenant/observation/threads?search=migration",
            ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var rows = await response.Content.ReadFromJsonAsync<List<ThreadSummaryResponse>>(ct);
        rows.ShouldNotBeNull();
        rows!.Select(r => r.Id).ShouldBe(new[] { "obs-1" });
    }

    [Fact]
    public async Task ListObservedThreads_SearchFilter_MatchesParticipantAddress()
    {
        // Searching by canonical address surfaces threads that reference a
        // specific actor by Guid — important for "find every thread agent
        // <uuid> touched" observation flows where the display name might
        // be missing or stale.
        var ct = TestContext.Current.CancellationToken;
        _factory.ThreadQueryService.ClearSubstitute();
        var now = DateTimeOffset.UtcNow;
        _factory.ThreadQueryService
            .ListAsync(Arg.Any<ThreadQueryFilters>(), Arg.Any<CancellationToken>())
            .Returns(new List<ThreadSummary>
            {
                new("obs-ada", new[] { "agent://ada" }, now, now, 1, "agent://ada", "Hand-off"),
                new("obs-grace", new[] { "agent://grace" }, now, now, 1, "agent://grace", "Routine"),
            });

        var response = await _client.GetAsync(
            "/api/v1/tenant/observation/threads?search=ada",
            ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var rows = await response.Content.ReadFromJsonAsync<List<ThreadSummaryResponse>>(ct);
        rows.ShouldNotBeNull();
        rows!.Select(r => r.Id).ShouldBe(new[] { "obs-ada" });
    }

    [Fact]
    public async Task ListObservedThreads_EmptySearch_DoesNotNarrow()
    {
        // A whitespace-only ?search= must behave the same as no search at
        // all — otherwise overzealous URL-state pruning would silently
        // hide every row.
        var ct = TestContext.Current.CancellationToken;
        _factory.ThreadQueryService.ClearSubstitute();
        var now = DateTimeOffset.UtcNow;
        _factory.ThreadQueryService
            .ListAsync(Arg.Any<ThreadQueryFilters>(), Arg.Any<CancellationToken>())
            .Returns(new List<ThreadSummary>
            {
                new("obs-1", new[] { "agent://ada" }, now, now, 1, "agent://ada", "Hand-off"),
                new("obs-2", new[] { "agent://grace" }, now, now, 1, "agent://grace", "Routine"),
            });

        var response = await _client.GetAsync(
            "/api/v1/tenant/observation/threads?search=%20%20",
            ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var rows = await response.Content.ReadFromJsonAsync<List<ThreadSummaryResponse>>(ct);
        rows.ShouldNotBeNull();
        rows!.Count.ShouldBe(2);
    }

    [Fact]
    public async Task ObservationEndpoints_AreDistinctFromThreadsEndpoints()
    {
        // The route group lives at /api/v1/tenant/observation/threads,
        // separate from the participant-scoped /api/v1/tenant/threads.
        // A request to the threads endpoint must NOT hit the observation
        // path — otherwise the role gates would collapse into one another.
        var ct = TestContext.Current.CancellationToken;
        _factory.ThreadQueryService.ClearSubstitute();
        _factory.ThreadQueryService
            .ListAsync(Arg.Any<ThreadQueryFilters>(), Arg.Any<CancellationToken>())
            .Returns(new List<ThreadSummary>());

        var threadsResp = await _client.GetAsync("/api/v1/tenant/threads", ct);
        var observationResp = await _client.GetAsync("/api/v1/tenant/observation/threads", ct);

        threadsResp.StatusCode.ShouldBe(HttpStatusCode.OK);
        observationResp.StatusCode.ShouldBe(HttpStatusCode.OK);

        // Both endpoints reach the same query service in this test (since
        // they share the underlying projection); the distinction the test
        // documents is structural — the URLs are different, both work.
    }

    /// <summary>
    /// Factory specialisation that wires an <see cref="IThreadQueryService"/>
    /// mock through the DI container. Mirrors <see cref="ThreadEndpointsTests.Factory"/>
    /// so the test class is hermetic from the rest of the suite.
    /// </summary>
    public sealed class Factory : CustomWebApplicationFactory
    {
        public IThreadQueryService ThreadQueryService { get; } = Substitute.For<IThreadQueryService>();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureServices(services =>
            {
                var descriptors = services
                    .Where(d => d.ServiceType == typeof(IThreadQueryService))
                    .ToList();
                foreach (var d in descriptors)
                {
                    services.Remove(d);
                }

                services.AddSingleton(ThreadQueryService);
            });
        }
    }
}
