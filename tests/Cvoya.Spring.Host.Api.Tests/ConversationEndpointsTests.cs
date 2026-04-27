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
public class ConversationEndpointsTests : IClassFixture<ConversationEndpointsTests.Factory>
{
    private readonly Factory _factory;
    private readonly HttpClient _client;

    public ConversationEndpointsTests(Factory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task ListConversations_NoFilters_ReturnsQueryServiceResult()
    {
        var ct = TestContext.Current.CancellationToken;
        var now = DateTimeOffset.UtcNow;
        _factory.ConversationQueryService
            .ListAsync(Arg.Any<ConversationQueryFilters>(), Arg.Any<CancellationToken>())
            .Returns(new List<ConversationSummary>
            {
                new("c-1", new[] { "agent://ada" }, "active", now, now, 1, "agent://ada", "Started"),
            });

        var response = await _client.GetAsync("/api/v1/tenant/conversations", ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var rows = await response.Content.ReadFromJsonAsync<List<ConversationSummary>>(ct);
        rows.ShouldNotBeNull();
        rows!.Count.ShouldBe(1);
        rows[0].Id.ShouldBe("c-1");
    }

    [Fact]
    public async Task ListConversations_WithFilters_PassesThemToService()
    {
        var ct = TestContext.Current.CancellationToken;
        _factory.ConversationQueryService
            .ListAsync(Arg.Any<ConversationQueryFilters>(), Arg.Any<CancellationToken>())
            .Returns(new List<ConversationSummary>());

        var response = await _client.GetAsync(
            "/api/v1/tenant/conversations?unit=eng-team&agent=ada&status=active&participant=human%3A%2F%2Fsavasp&limit=25",
            ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        await _factory.ConversationQueryService.Received(1)
            .ListAsync(
                Arg.Is<ConversationQueryFilters>(f =>
                    f.Unit == "eng-team" &&
                    f.Agent == "ada" &&
                    f.Status == "active" &&
                    f.Participant == "human://savasp" &&
                    f.Limit == 25),
                Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetConversation_Missing_Returns404()
    {
        var ct = TestContext.Current.CancellationToken;
        _factory.ConversationQueryService
            .GetAsync("c-missing", Arg.Any<CancellationToken>())
            .Returns((ConversationDetail?)null);

        var response = await _client.GetAsync("/api/v1/tenant/conversations/c-missing", ct);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task GetConversation_Existing_ReturnsDetail()
    {
        var ct = TestContext.Current.CancellationToken;
        var now = DateTimeOffset.UtcNow;
        _factory.ConversationQueryService
            .GetAsync("c-1", Arg.Any<CancellationToken>())
            .Returns(new ConversationDetail(
                new ConversationSummary("c-1", new[] { "agent://ada" }, "active", now, now, 1, "agent://ada", "s"),
                new List<ConversationEvent>
                {
                    new(Guid.NewGuid(), now, "agent://ada", "ConversationStarted", "Info", "Started conversation c-1"),
                }));

        var response = await _client.GetAsync("/api/v1/tenant/conversations/c-1", ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var detail = await response.Content.ReadFromJsonAsync<ConversationDetail>(ct);
        detail.ShouldNotBeNull();
        detail!.Summary.Id.ShouldBe("c-1");
        detail.Events.Count.ShouldBe(1);
    }

    [Fact]
    public async Task PostConversationMessage_RoutesThroughMessageRouter()
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

        var body = new ConversationMessageRequest(
            new AddressDto("agent", "ada"),
            "Looks good — ship it.");

        var response = await _client.PostAsJsonAsync("/api/v1/tenant/conversations/c-1/messages", body, ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var result = await response.Content.ReadFromJsonAsync<ConversationMessageResponse>(ct);
        result.ShouldNotBeNull();
        result!.ConversationId.ShouldBe("c-1");

        await _factory.MessageRouter.Received(1).RouteAsync(
            Arg.Is<Message>(m =>
                m.ConversationId == "c-1" &&
                m.Type == MessageType.Domain &&
                m.To.Scheme == "agent" &&
                m.To.Path == "ada"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PostConversationMessage_PermissionDenied_Returns403()
    {
        var ct = TestContext.Current.CancellationToken;
        // #499: See PostConversationMessage_RoutesThroughMessageRouter for
        // why ClearSubstitute (instead of ClearReceivedCalls) is required.
        _factory.MessageRouter.ClearSubstitute();
        _factory.MessageRouter
            .RouteAsync(Arg.Any<Message>(), Arg.Any<CancellationToken>())
            .Returns(Result<Message?, RoutingError>.Failure(
                RoutingError.PermissionDenied(new Address("agent", "ada"))));

        var body = new ConversationMessageRequest(new AddressDto("agent", "ada"), "hi");

        var response = await _client.PostAsJsonAsync("/api/v1/tenant/conversations/c-1/messages", body, ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task PostConversationMessage_CallerValidation_Returns400WithCode()
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
                    new Address("agent", "ada"),
                    CallerValidationCodes.UnknownMessageType,
                    "Unknown message type: Amendment")));

        var body = new ConversationMessageRequest(new AddressDto("agent", "ada"), "hi");

        var response = await _client.PostAsJsonAsync("/api/v1/tenant/conversations/c-1/messages", body, ct);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var problem = await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>(ct);
        problem.GetProperty("detail").GetString().ShouldBe("Unknown message type: Amendment");
        problem.GetProperty("code").GetString().ShouldBe(CallerValidationCodes.UnknownMessageType);
    }

    [Fact]
    public async Task PostConversationMessage_DeliveryFailed_Returns502()
    {
        // Regression guard: genuine downstream failures still map to 502,
        // so the #993 recasting doesn't silently swallow real outages.
        var ct = TestContext.Current.CancellationToken;
        _factory.MessageRouter.ClearSubstitute();
        _factory.MessageRouter
            .RouteAsync(Arg.Any<Message>(), Arg.Any<CancellationToken>())
            .Returns(Result<Message?, RoutingError>.Failure(
                RoutingError.DeliveryFailed(new Address("agent", "ada"), "Actor unavailable")));

        var body = new ConversationMessageRequest(new AddressDto("agent", "ada"), "hi");

        var response = await _client.PostAsJsonAsync("/api/v1/tenant/conversations/c-1/messages", body, ct);

        response.StatusCode.ShouldBe(HttpStatusCode.BadGateway);
    }

    // --- #1038 — POST /api/v1/conversations/{id}/close ---

    [Fact]
    public async Task CloseConversation_Existing_CallsAgentActorAndReturnsRefreshedDetail()
    {
        var ct = TestContext.Current.CancellationToken;
        var now = DateTimeOffset.UtcNow;

        var beforeSummary = new ConversationSummary(
            "c-close", new[] { "agent://ada", "human://savasp" },
            "active", now, now, 1, "agent://ada", "Started");
        var beforeDetail = new ConversationDetail(beforeSummary, new List<ConversationEvent>());

        var afterSummary = beforeSummary with { Status = "closed" };
        var afterDetail = new ConversationDetail(
            afterSummary,
            new List<ConversationEvent>
            {
                new(Guid.NewGuid(), now, "agent://ada", "ConversationClosed", "Info", "Conversation closed (operator request)"),
            });

        // First GetAsync returns the live conversation; second (post-close) returns
        // the projected detail with the ConversationClosed event included.
        _factory.ConversationQueryService.ClearSubstitute();
        _factory.ConversationQueryService.GetAsync("c-close", Arg.Any<CancellationToken>())
            .Returns(beforeDetail, afterDetail);

        var entry = new DirectoryEntry(
            new Address("agent", "ada"),
            ActorId: "ada",
            DisplayName: "Ada",
            Description: "Test agent",
            Role: null,
            RegisteredAt: now);
        _factory.DirectoryService.ClearSubstitute();
        _factory.DirectoryService.ResolveAsync(
                Arg.Is<Address>(a => a.Scheme == "agent" && a.Path == "ada"),
                Arg.Any<CancellationToken>())
            .Returns(entry);

        var agentProxy = Substitute.For<IAgentActor>();
        _factory.ActorProxyFactory.ClearSubstitute();
        _factory.ActorProxyFactory
            .CreateActorProxy<IAgentActor>(Arg.Is<ActorId>(id => id.GetId() == "ada"), nameof(AgentActor))
            .Returns(agentProxy);

        var body = new CloseConversationRequest("operator request");
        var response = await _client.PostAsJsonAsync("/api/v1/tenant/conversations/c-close/close", body, ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var detail = await response.Content.ReadFromJsonAsync<ConversationDetail>(ct);
        detail.ShouldNotBeNull();
        detail!.Summary.Status.ShouldBe("closed");
        detail.Events.Count.ShouldBe(1);
        detail.Events[0].EventType.ShouldBe("ConversationClosed");

        await agentProxy.Received(1).CloseConversationAsync(
            "c-close", "operator request", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CloseConversation_Missing_Returns404()
    {
        var ct = TestContext.Current.CancellationToken;
        _factory.ConversationQueryService.ClearSubstitute();
        _factory.ConversationQueryService.GetAsync("c-missing", Arg.Any<CancellationToken>())
            .Returns((ConversationDetail?)null);

        var body = new CloseConversationRequest("oops");
        var response = await _client.PostAsJsonAsync("/api/v1/tenant/conversations/c-missing/close", body, ct);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task CloseConversation_NoAgentParticipants_StillReturnsOk()
    {
        // Conversations with only human participants have no actor proxies to
        // call — the endpoint must still succeed (and return the unchanged
        // detail) so operator UX doesn't break for human-only threads.
        var ct = TestContext.Current.CancellationToken;
        var now = DateTimeOffset.UtcNow;
        var summary = new ConversationSummary(
            "c-human-only", new[] { "human://savasp" },
            "active", now, now, 0, "human://savasp", "Pending input");
        var detail = new ConversationDetail(summary, new List<ConversationEvent>());

        _factory.ConversationQueryService.ClearSubstitute();
        _factory.ConversationQueryService.GetAsync("c-human-only", Arg.Any<CancellationToken>())
            .Returns(detail);
        // ClearSubstitute on the shared ActorProxyFactory so the
        // DidNotReceive assertion below isn't polluted by other tests in
        // this class fixture that exercise the close endpoint.
        _factory.ActorProxyFactory.ClearSubstitute();

        var body = new CloseConversationRequest(null);
        var response = await _client.PostAsJsonAsync("/api/v1/tenant/conversations/c-human-only/close", body, ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        _factory.ActorProxyFactory.DidNotReceive()
            .CreateActorProxy<IAgentActor>(Arg.Any<ActorId>(), Arg.Any<string>());
    }

    [Fact]
    public async Task ListInbox_ReturnsQueryServiceRows()
    {
        var ct = TestContext.Current.CancellationToken;
        var now = DateTimeOffset.UtcNow;
        _factory.ConversationQueryService
            .ListInboxAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new List<InboxItem>
            {
                new("c-9", "agent://ada", "human://local-dev-user", now, "Approve merge?"),
            });

        var response = await _client.GetAsync("/api/v1/tenant/inbox", ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var rows = await response.Content.ReadFromJsonAsync<List<InboxItem>>(ct);
        rows.ShouldNotBeNull();
        rows!.Count.ShouldBe(1);
        rows[0].ConversationId.ShouldBe("c-9");
    }

    /// <summary>
    /// Factory specialisation that wires an <see cref="IConversationQueryService"/>
    /// mock through the DI container. We subclass
    /// <see cref="CustomWebApplicationFactory"/> so existing fixtures stay
    /// untouched while this file still gets the full Dapr-swap-out treatment.
    /// </summary>
    public sealed class Factory : CustomWebApplicationFactory
    {
        public IConversationQueryService ConversationQueryService { get; } = Substitute.For<IConversationQueryService>();

        public IMessageRouter MessageRouter { get; } = Substitute.For<IMessageRouter>();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureServices(services =>
            {
                var descriptors = services
                    .Where(d => d.ServiceType == typeof(IConversationQueryService)
                             || d.ServiceType == typeof(IMessageRouter))
                    .ToList();
                foreach (var d in descriptors)
                {
                    services.Remove(d);
                }

                services.AddSingleton(ConversationQueryService);
                services.AddSingleton(MessageRouter);
            });
        }
    }
}