// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Api.Tests;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using Cvoya.Spring.Core;
using Cvoya.Spring.Core.Directory;
using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Core.Observability;
using Cvoya.Spring.Dapr.Actors;
using Cvoya.Spring.Dapr.Data;
using Cvoya.Spring.Dapr.Data.Entities;
using Cvoya.Spring.Host.Api.Models;

using global::Dapr.Actors;

using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

using NSubstitute;
using NSubstitute.ClearExtensions;

using Shouldly;

using Xunit;

/// <summary>
/// Integration tests for the conversation + inbox endpoints shipped with
/// #452 / #456. The query service is mocked so these tests stay focused on
/// the HTTP plumbing — parameter binding, 404/400 branches, body shape —
/// without having to spin up the real EF-backed projection.
/// </summary>
public class ThreadEndpointsTests : IClassFixture<ThreadEndpointsTests.Factory>
{
    private static readonly Guid Agent_Ada_Id = new("00000001-feed-1234-5678-000000000000");

    private readonly Factory _factory;
    private readonly HttpClient _client;

    public ThreadEndpointsTests(Factory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task ListThreads_NoFilters_ReturnsQueryServiceResult()
    {
        var ct = TestContext.Current.CancellationToken;
        var now = DateTimeOffset.UtcNow;
        _factory.ThreadQueryService
            .ListAsync(Arg.Any<ThreadQueryFilters>(), Arg.Any<CancellationToken>())
            .Returns(new List<ThreadSummary>
            {
                new("c-1", new[] { "agent://ada" }, "active", now, now, 1, "agent://ada", "Started"),
            });

        var response = await _client.GetAsync("/api/v1/tenant/threads", ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var rows = await response.Content.ReadFromJsonAsync<List<ThreadSummaryResponse>>(ct);
        rows.ShouldNotBeNull();
        rows!.Count.ShouldBe(1);
        rows[0].Id.ShouldBe("c-1");
    }

    [Fact]
    public async Task ListThreads_WithFilters_PassesThemToService()
    {
        var ct = TestContext.Current.CancellationToken;
        _factory.ThreadQueryService
            .ListAsync(Arg.Any<ThreadQueryFilters>(), Arg.Any<CancellationToken>())
            .Returns(new List<ThreadSummary>());

        var response = await _client.GetAsync(
            "/api/v1/tenant/threads?unit=eng-team&agent=ada&status=active&participant=human%3A%2F%2Fsavasp&limit=25",
            ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        await _factory.ThreadQueryService.Received(1)
            .ListAsync(
                Arg.Is<ThreadQueryFilters>(f =>
                    f.Unit == "eng-team" &&
                    f.Agent == "ada" &&
                    f.Status == "active" &&
                    f.Participant == "human://savasp" &&
                    f.Limit == 25),
                Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetThread_Missing_Returns404()
    {
        var ct = TestContext.Current.CancellationToken;
        _factory.ThreadQueryService
            .GetAsync("c-missing", Arg.Any<CancellationToken>())
            .Returns((ThreadDetail?)null);

        var response = await _client.GetAsync("/api/v1/tenant/threads/c-missing", ct);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task ListThreads_AgentIdentityFormParticipant_ResolvesToAgentDisplayName()
    {
        // #1545 / #1547 / #1548: NormaliseSource emits agent participants as
        // "agent:id:<uuid>" whenever the activity event was persisted with
        // the actor UUID as the source. The display-name resolver must look
        // the agent up by ActorId so the response carries the agent's
        // human-readable name instead of the bare UUID — without this fix
        // the portal renders a raw UUID in every thread list row, message
        // bubble, and explorer header.
        var ct = TestContext.Current.CancellationToken;
        var now = DateTimeOffset.UtcNow;
        var agentGuid = Guid.NewGuid();
        var agentActorId = agentGuid.ToString("D");

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<SpringDbContext>();
            db.AgentDefinitions.Add(new AgentDefinitionEntity
            {
                Id = agentGuid,
                DisplayName = "Ada Lovelace",
                TenantId = Cvoya.Spring.Core.Tenancy.OssTenantIds.Default,
                CreatedAt = now,
                UpdatedAt = now,
            });
            await db.SaveChangesAsync(ct);
        }

        var participantAddress = $"agent:id:{agentActorId}";
        _factory.ThreadQueryService
            .ListAsync(Arg.Any<ThreadQueryFilters>(), Arg.Any<CancellationToken>())
            .Returns(new List<ThreadSummary>
            {
                new("c-id-form", new[] { participantAddress }, "active", now, now, 1, participantAddress, "Started"),
            });

        var response = await _client.GetAsync("/api/v1/tenant/threads", ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var rows = await response.Content.ReadFromJsonAsync<List<ThreadSummaryResponse>>(ct);
        rows.ShouldNotBeNull();
        var participant = rows!.Single().Participants.Single();
        participant.Address.ShouldBe(participantAddress);
        participant.DisplayName.ShouldBe("Ada Lovelace");
    }

    [Fact]
    public async Task ListThreads_UnitIdentityFormParticipant_ResolvesToUnitDisplayName()
    {
        // Same path as the agent test above for unit:id:<uuid> participants.
        var ct = TestContext.Current.CancellationToken;
        var now = DateTimeOffset.UtcNow;
        var unitGuid = Guid.NewGuid();
        var unitActorId = unitGuid.ToString("D");

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<SpringDbContext>();
            db.UnitDefinitions.Add(new UnitDefinitionEntity
            {
                Id = unitGuid,
                DisplayName = "Engineering",
                TenantId = Cvoya.Spring.Core.Tenancy.OssTenantIds.Default,
                CreatedAt = now,
                UpdatedAt = now,
            });
            await db.SaveChangesAsync(ct);
        }

        var participantAddress = $"unit:id:{unitActorId}";
        _factory.ThreadQueryService
            .ListAsync(Arg.Any<ThreadQueryFilters>(), Arg.Any<CancellationToken>())
            .Returns(new List<ThreadSummary>
            {
                new("c-unit-id", new[] { participantAddress }, "active", now, now, 1, participantAddress, "Started"),
            });

        var response = await _client.GetAsync("/api/v1/tenant/threads", ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var rows = await response.Content.ReadFromJsonAsync<List<ThreadSummaryResponse>>(ct);
        rows.ShouldNotBeNull();
        var participant = rows!.Single().Participants.Single();
        participant.Address.ShouldBe(participantAddress);
        participant.DisplayName.ShouldBe("Engineering");
    }

    [Fact]
    public async Task GetThread_Existing_ReturnsDetail()
    {
        var ct = TestContext.Current.CancellationToken;
        var now = DateTimeOffset.UtcNow;
        _factory.ThreadQueryService
            .GetAsync("c-1", Arg.Any<CancellationToken>())
            .Returns(new ThreadDetail(
                new ThreadSummary("c-1", new[] { "agent://ada" }, "active", now, now, 1, "agent://ada", "s"),
                new List<ThreadEvent>
                {
                    new(Guid.NewGuid(), now, "agent://ada", "ThreadStarted", "Info", "Started conversation c-1"),
                }));

        var response = await _client.GetAsync("/api/v1/tenant/threads/c-1", ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var detail = await response.Content.ReadFromJsonAsync<ThreadDetailResponse>(ct);
        detail.ShouldNotBeNull();
        detail!.Summary.ShouldNotBeNull();
        detail.Summary!.Id.ShouldBe("c-1");
        detail.Events.ShouldNotBeNull();
        detail.Events!.Count.ShouldBe(1);
    }

    [Fact]
    public async Task PostThreadMessage_RoutesThroughMessageRouter()
    {
        var ct = TestContext.Current.CancellationToken;
        // #499: Use ClearSubstitute so both received-call history AND any
        // prior arrangements on RouteAsync are wiped before this test
        // arranges its own. The two PostConversationMessage_* tests share
        // the class-fixture MessageRouter mock; ClearReceivedCalls alone
        // leaves stale Returns/Throws configurations in place, which is the
        // "shared-mock-state hazard" called out in #499.
        _factory.MessageRouter.ClearSubstitute();
        _factory.MessageRouter
            .RouteAsync(Arg.Any<Message>(), Arg.Any<CancellationToken>())
            .Returns(Result<Message?, RoutingError>.Success(null));

        var adaHex = Agent_Ada_Id.ToString("N");
        var body = new ThreadMessageRequest(
            new AddressDto("agent", adaHex),
            "Looks good — ship it.");

        var response = await _client.PostAsJsonAsync("/api/v1/tenant/threads/c-1/messages", body, ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var result = await response.Content.ReadFromJsonAsync<ThreadMessageResponse>(ct);
        result.ShouldNotBeNull();
        result!.ThreadId.ShouldBe("c-1");

        await _factory.MessageRouter.Received(1).RouteAsync(
            Arg.Is<Message>(m =>
                m.ThreadId == "c-1" &&
                m.Type == MessageType.Domain &&
                m.To.Scheme == "agent" &&
                m.To.Id == Agent_Ada_Id),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PostThreadMessage_PermissionDenied_Returns403()
    {
        var ct = TestContext.Current.CancellationToken;
        // #499: See PostThreadMessage_RoutesThroughMessageRouter for
        // why ClearSubstitute (instead of ClearReceivedCalls) is required.
        _factory.MessageRouter.ClearSubstitute();
        _factory.MessageRouter
            .RouteAsync(Arg.Any<Message>(), Arg.Any<CancellationToken>())
            .Returns(Result<Message?, RoutingError>.Failure(
                RoutingError.PermissionDenied(new Address("agent", Agent_Ada_Id))));

        var body = new ThreadMessageRequest(new AddressDto("agent", Agent_Ada_Id.ToString("N")), "hi");

        var response = await _client.PostAsJsonAsync("/api/v1/tenant/threads/c-1/messages", body, ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task PostThreadMessage_CallerValidation_Returns400WithCode()
    {
        // #993: CallerValidation routing errors thread through the
        // conversation-messaging endpoint the same way they do on
        // /api/v1/messages — 400 with the stable `code` extension.
        var ct = TestContext.Current.CancellationToken;
        _factory.MessageRouter.ClearSubstitute();
        _factory.MessageRouter
            .RouteAsync(Arg.Any<Message>(), Arg.Any<CancellationToken>())
            .Returns(Result<Message?, RoutingError>.Failure(
                RoutingError.CallerValidation(
                    new Address("agent", Agent_Ada_Id),
                    CallerValidationCodes.UnknownMessageType,
                    "Unknown message type: Amendment")));

        var body = new ThreadMessageRequest(new AddressDto("agent", Agent_Ada_Id.ToString("N")), "hi");

        var response = await _client.PostAsJsonAsync("/api/v1/tenant/threads/c-1/messages", body, ct);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var problem = await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>(ct);
        problem.GetProperty("detail").GetString().ShouldBe("Unknown message type: Amendment");
        problem.GetProperty("code").GetString().ShouldBe(CallerValidationCodes.UnknownMessageType);
    }

    [Fact]
    public async Task PostThreadMessage_DeliveryFailed_Returns502()
    {
        // Regression guard: genuine downstream failures still map to 502,
        // so the #993 recasting doesn't silently swallow real outages.
        var ct = TestContext.Current.CancellationToken;
        _factory.MessageRouter.ClearSubstitute();
        _factory.MessageRouter
            .RouteAsync(Arg.Any<Message>(), Arg.Any<CancellationToken>())
            .Returns(Result<Message?, RoutingError>.Failure(
                RoutingError.DeliveryFailed(new Address("agent", Agent_Ada_Id), "Actor unavailable")));

        var body = new ThreadMessageRequest(new AddressDto("agent", Agent_Ada_Id.ToString("N")), "hi");

        var response = await _client.PostAsJsonAsync("/api/v1/tenant/threads/c-1/messages", body, ct);

        response.StatusCode.ShouldBe(HttpStatusCode.BadGateway);
    }

    // --- #1038 + #1207 — POST /api/v1/threads/{id}/close ---
    // #1207 regression guard: the close path MUST invoke AgentActor.CloseConversationAsync
    // so the actor clears its ActiveConversation slot. Without this call the actor stays
    // "bricked" — every subsequent message send queues forever until worker restart.

    [Fact]
    public async Task CloseThread_Existing_CallsAgentActorAndReturnsRefreshedDetail()
    {
        var ct = TestContext.Current.CancellationToken;
        var now = DateTimeOffset.UtcNow;

        var adaParticipant = $"agent://{Agent_Ada_Id:N}";
        var beforeSummary = new ThreadSummary(
            "c-close", new[] { adaParticipant, "human://savasp" },
            "active", now, now, 1, adaParticipant, "Started");
        var beforeDetail = new ThreadDetail(beforeSummary, new List<ThreadEvent>());

        var afterSummary = beforeSummary with { Status = "closed" };
        var afterDetail = new ThreadDetail(
            afterSummary,
            new List<ThreadEvent>
            {
                new(Guid.NewGuid(), now, adaParticipant, "ThreadClosed", "Info", "Thread closed (operator request)"),
            });

        // First GetAsync returns the live conversation; second (post-close) returns
        // the projected detail with the ThreadClosed event included.
        _factory.ThreadQueryService.ClearSubstitute();
        _factory.ThreadQueryService.GetAsync("c-close", Arg.Any<CancellationToken>())
            .Returns(beforeDetail, afterDetail);

        var entry = new DirectoryEntry(
            new Address("agent", Agent_Ada_Id),
            ActorId: Agent_Ada_Id,
            DisplayName: "Ada",
            Description: "Test agent",
            Role: null,
            RegisteredAt: now);
        _factory.DirectoryService.ClearSubstitute();
        _factory.DirectoryService.ResolveAsync(
                Arg.Is<Address>(a => a.Scheme == "agent" && a.Id == Agent_Ada_Id),
                Arg.Any<CancellationToken>())
            .Returns(entry);

        var adaActorIdStr = Agent_Ada_Id.ToString("N");
        var agentProxy = Substitute.For<IAgentActor>();
        _factory.ActorProxyFactory.ClearSubstitute();
        _factory.ActorProxyFactory
            .CreateActorProxy<IAgentActor>(Arg.Is<ActorId>(id => id.GetId() == adaActorIdStr), nameof(AgentActor))
            .Returns(agentProxy);

        var body = new CloseThreadRequest("operator request");
        var response = await _client.PostAsJsonAsync("/api/v1/tenant/threads/c-close/close", body, ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var detail = await response.Content.ReadFromJsonAsync<ThreadDetailResponse>(ct);
        detail.ShouldNotBeNull();
        detail!.Summary.ShouldNotBeNull();
        detail.Summary!.Status.ShouldBe("closed");
        detail.Events.ShouldNotBeNull();
        detail.Events!.Count.ShouldBe(1);
        detail.Events[0].EventType.ShouldBe("ThreadClosed");

        await agentProxy.Received(1).CloseConversationAsync(
            "c-close", "operator request", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CloseThread_Missing_Returns404()
    {
        var ct = TestContext.Current.CancellationToken;
        _factory.ThreadQueryService.ClearSubstitute();
        _factory.ThreadQueryService.GetAsync("c-missing", Arg.Any<CancellationToken>())
            .Returns((ThreadDetail?)null);

        var body = new CloseThreadRequest("oops");
        var response = await _client.PostAsJsonAsync("/api/v1/tenant/threads/c-missing/close", body, ct);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task CloseThread_NoAgentParticipants_StillReturnsOk()
    {
        // Conversations with only human participants have no actor proxies to
        // call — the endpoint must still succeed (and return the unchanged
        // detail) so operator UX doesn't break for human-only threads.
        var ct = TestContext.Current.CancellationToken;
        var now = DateTimeOffset.UtcNow;
        var summary = new ThreadSummary(
            "c-human-only", new[] { "human://savasp" },
            "active", now, now, 0, "human://savasp", "Pending input");
        var detail = new ThreadDetail(summary, new List<ThreadEvent>());

        _factory.ThreadQueryService.ClearSubstitute();
        _factory.ThreadQueryService.GetAsync("c-human-only", Arg.Any<CancellationToken>())
            .Returns(detail);
        // ClearSubstitute on the shared ActorProxyFactory so the
        // DidNotReceive assertion below isn't polluted by other tests in
        // this class fixture that exercise the close endpoint.
        _factory.ActorProxyFactory.ClearSubstitute();

        var body = new CloseThreadRequest(null);
        var response = await _client.PostAsJsonAsync("/api/v1/tenant/threads/c-human-only/close", body, ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        _factory.ActorProxyFactory.DidNotReceive()
            .CreateActorProxy<IAgentActor>(Arg.Any<ActorId>(), Arg.Any<string>());
    }

    [Fact]
    public async Task ListInbox_ReturnsQueryServiceRows()
    {
        var ct = TestContext.Current.CancellationToken;
        var now = DateTimeOffset.UtcNow;
        _factory.ThreadQueryService
            .ListInboxAsync(Arg.Any<string>(), Arg.Any<IReadOnlyDictionary<string, DateTimeOffset>?>(), Arg.Any<CancellationToken>())
            .Returns(new List<InboxItem>
            {
                new("c-9", "agent://ada", "human://local-dev-user", now, "Approve merge?"),
            });

        var response = await _client.GetAsync("/api/v1/tenant/inbox", ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var rows = await response.Content.ReadFromJsonAsync<List<InboxItemResponse>>(ct);
        rows.ShouldNotBeNull();
        rows!.Count.ShouldBe(1);
        rows[0].ThreadId.ShouldBe("c-9");
    }

    /// <summary>
    /// Factory specialisation that wires an <see cref="IThreadQueryService"/>
    /// mock through the DI container. We subclass
    /// <see cref="CustomWebApplicationFactory"/> so existing fixtures stay
    /// untouched while this file still gets the full Dapr-swap-out treatment.
    /// </summary>
    public sealed class Factory : CustomWebApplicationFactory
    {
        public IThreadQueryService ThreadQueryService { get; } = Substitute.For<IThreadQueryService>();

        public IMessageRouter MessageRouter { get; } = Substitute.For<IMessageRouter>();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureServices(services =>
            {
                var descriptors = services
                    .Where(d => d.ServiceType == typeof(IThreadQueryService)
                             || d.ServiceType == typeof(IMessageRouter))
                    .ToList();
                foreach (var d in descriptors)
                {
                    services.Remove(d);
                }

                services.AddSingleton(ThreadQueryService);
                services.AddSingleton(MessageRouter);
            });
        }
    }
}