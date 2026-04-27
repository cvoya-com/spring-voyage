// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Api.Tests.Contract;

using System.Net;
using System.Net.Http.Json;

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
/// Semantic contract tests for the <c>/api/v1/conversations</c> surface
/// (#1248 / C1.3). The conversation read-side talks to <see cref="IConversationQueryService"/>;
/// we wire a substitute via a custom factory subclass so contract tests
/// stay focused on wire shape without spinning up the EF projection.
/// </summary>
public class ConversationContractTests : IClassFixture<ConversationContractTests.Factory>
{
    private readonly Factory _factory;
    private readonly HttpClient _client;

    public ConversationContractTests(Factory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task ListConversations_MatchesContract()
    {
        var ct = TestContext.Current.CancellationToken;
        var now = DateTimeOffset.UtcNow;
        _factory.ConversationQueryService.ClearSubstitute();
        _factory.ConversationQueryService
            .ListAsync(Arg.Any<ConversationQueryFilters>(), Arg.Any<CancellationToken>())
            .Returns(new List<ConversationSummary>
            {
                new("contract-conv-list", new[] { "agent://contract-bot" },
                    "active", now, now, 1, "agent://contract-bot", "Started"),
            });

        var response = await _client.GetAsync("/api/v1/tenant/conversations", ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var body = await response.Content.ReadAsStringAsync(ct);
        OpenApiContract.AssertResponse("/api/v1/tenant/conversations", "get", "200", body);
    }

    [Fact]
    public async Task GetConversation_HappyPath_MatchesContract()
    {
        var ct = TestContext.Current.CancellationToken;
        var now = DateTimeOffset.UtcNow;
        _factory.ConversationQueryService.ClearSubstitute();
        _factory.ConversationQueryService
            .GetAsync("contract-conv-detail", Arg.Any<CancellationToken>())
            .Returns(new ConversationDetail(
                new ConversationSummary("contract-conv-detail",
                    new[] { "agent://contract-bot" },
                    "active", now, now, 1, "agent://contract-bot", "Started"),
                new List<ConversationEvent>
                {
                    new(Guid.NewGuid(), now, "agent://contract-bot",
                        "ConversationStarted", "Info", "Started"),
                }));

        var response = await _client.GetAsync("/api/v1/tenant/conversations/contract-conv-detail", ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var body = await response.Content.ReadAsStringAsync(ct);
        OpenApiContract.AssertResponse(
            "/api/v1/tenant/conversations/{id}", "get", "200", body);
    }

    [Fact]
    public async Task GetConversation_NotFound_MatchesProblemDetailsContract()
    {
        var ct = TestContext.Current.CancellationToken;
        _factory.ConversationQueryService.ClearSubstitute();
        _factory.ConversationQueryService
            .GetAsync("contract-conv-missing", Arg.Any<CancellationToken>())
            .Returns((ConversationDetail?)null);

        var response = await _client.GetAsync("/api/v1/tenant/conversations/contract-conv-missing", ct);
        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);

        var body = await response.Content.ReadAsStringAsync(ct);
        OpenApiContract.AssertResponse(
            "/api/v1/tenant/conversations/{id}", "get", "404", body, "application/problem+json");
    }

    [Fact]
    public async Task PostConversationMessage_HappyPath_MatchesContract()
    {
        var ct = TestContext.Current.CancellationToken;
        _factory.MessageRouter.ClearSubstitute();

        // Return a non-null reply so ConversationMessageResponse.responsePayload
        // is a JSON object on the wire. The committed openapi.json declares it
        // as `oneOf: [null, JsonElement]` (with JsonElement = empty schema);
        // a null payload matches both branches and oneOf rejects. See follow-up
        // for the spec cleanup.
        var reply = new Message(
            Guid.NewGuid(),
            new Address("agent", "contract-bot"),
            new Address("human", "local-dev-user"),
            MessageType.Domain,
            "contract-conv-post",
            System.Text.Json.JsonSerializer.SerializeToElement(new { ack = "received" }),
            DateTimeOffset.UtcNow);
        _factory.MessageRouter
            .RouteAsync(Arg.Any<Message>(), Arg.Any<CancellationToken>())
            .Returns(Result<Message?, RoutingError>.Success(reply));

        var body = new ConversationMessageRequest(
            new AddressDto("agent", "contract-bot"),
            "Hello from contract test");

        var response = await _client.PostAsJsonAsync(
            "/api/v1/tenant/conversations/contract-conv-post/messages", body, ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var responseBody = await response.Content.ReadAsStringAsync(ct);
        OpenApiContract.AssertResponse(
            "/api/v1/tenant/conversations/{id}/messages", "post", "200", responseBody);
    }

    [Fact]
    public async Task CloseConversation_NotFound_MatchesProblemDetailsContract()
    {
        var ct = TestContext.Current.CancellationToken;
        _factory.ConversationQueryService.ClearSubstitute();
        _factory.ConversationQueryService
            .GetAsync("contract-close-missing", Arg.Any<CancellationToken>())
            .Returns((ConversationDetail?)null);

        var body = new CloseConversationRequest("contract test");
        var response = await _client.PostAsJsonAsync(
            "/api/v1/tenant/conversations/contract-close-missing/close", body, ct);
        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);

        var responseBody = await response.Content.ReadAsStringAsync(ct);
        OpenApiContract.AssertResponse(
            "/api/v1/tenant/conversations/{id}/close", "post", "404",
            responseBody, "application/problem+json");
    }

    [Fact]
    public async Task CloseConversation_HappyPath_MatchesContract()
    {
        var ct = TestContext.Current.CancellationToken;
        var now = DateTimeOffset.UtcNow;
        var summary = new ConversationSummary(
            "contract-close-ok",
            new[] { "agent://contract-bot" },
            "active", now, now, 1, "agent://contract-bot", "Started");
        var beforeDetail = new ConversationDetail(summary, new List<ConversationEvent>());
        var afterDetail = new ConversationDetail(
            summary with { Status = "closed" },
            new List<ConversationEvent>
            {
                new(Guid.NewGuid(), now, "agent://contract-bot",
                    "ConversationClosed", "Info", "Closed"),
            });

        _factory.ConversationQueryService.ClearSubstitute();
        _factory.ConversationQueryService
            .GetAsync("contract-close-ok", Arg.Any<CancellationToken>())
            .Returns(beforeDetail, afterDetail);

        var entry = new DirectoryEntry(
            new Address("agent", "contract-bot"),
            ActorId: "actor-contract-bot",
            DisplayName: "Bot",
            Description: "",
            Role: null,
            RegisteredAt: now);
        _factory.DirectoryService.ClearSubstitute();
        _factory.DirectoryService
            .ResolveAsync(
                Arg.Is<Address>(a => a.Scheme == "agent" && a.Path == "contract-bot"),
                Arg.Any<CancellationToken>())
            .Returns(entry);

        var agentProxy = Substitute.For<IAgentActor>();
        _factory.ActorProxyFactory.ClearSubstitute();
        _factory.ActorProxyFactory
            .CreateActorProxy<IAgentActor>(
                Arg.Is<ActorId>(id => id.GetId() == "actor-contract-bot"),
                nameof(AgentActor))
            .Returns(agentProxy);

        var body = new CloseConversationRequest("contract test");
        var response = await _client.PostAsJsonAsync(
            "/api/v1/tenant/conversations/contract-close-ok/close", body, ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var responseBody = await response.Content.ReadAsStringAsync(ct);
        OpenApiContract.AssertResponse(
            "/api/v1/tenant/conversations/{id}/close", "post", "200", responseBody);
    }

    /// <summary>
    /// Custom factory that swaps the conversation query service and message
    /// router for substitutes — mirrors <c>ConversationEndpointsTests.Factory</c>.
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