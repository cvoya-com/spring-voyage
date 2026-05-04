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
/// (#1248 / C1.3). The conversation read-side talks to <see cref="IThreadQueryService"/>;
/// we wire a substitute via a custom factory subclass so contract tests
/// stay focused on wire shape without spinning up the EF projection.
/// </summary>
public class ThreadContractTests : IClassFixture<ThreadContractTests.Factory>
{
    private static readonly Guid ActorContractBot_Id = new("00002711-bbbb-cccc-dddd-000000000000");

    private static readonly Guid Agent_ContractBot_Id = new("00000001-feed-1234-5678-000000000000");
    private static readonly Guid Human_LocalDevUser_Id = new("00000002-feed-1234-5678-000000000000");

    private readonly Factory _factory;
    private readonly HttpClient _client;

    public ThreadContractTests(Factory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task ListThreads_MatchesContract()
    {
        var ct = TestContext.Current.CancellationToken;
        var now = DateTimeOffset.UtcNow;
        _factory.ThreadQueryService.ClearSubstitute();
        _factory.ThreadQueryService
            .ListAsync(Arg.Any<ThreadQueryFilters>(), Arg.Any<CancellationToken>())
            .Returns(new List<ThreadSummary>
            {
                new("contract-conv-list", new[] { "agent://contract-bot" },
                    "active", now, now, 1, "agent://contract-bot", "Started"),
            });

        var response = await _client.GetAsync("/api/v1/tenant/threads", ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var body = await response.Content.ReadAsStringAsync(ct);
        OpenApiContract.AssertResponse("/api/v1/tenant/threads", "get", "200", body);
    }

    [Fact]
    public async Task GetThread_HappyPath_MatchesContract()
    {
        var ct = TestContext.Current.CancellationToken;
        var now = DateTimeOffset.UtcNow;
        _factory.ThreadQueryService.ClearSubstitute();
        _factory.ThreadQueryService
            .GetAsync("contract-conv-detail", Arg.Any<CancellationToken>())
            .Returns(new ThreadDetail(
                new ThreadSummary("contract-conv-detail",
                    new[] { "agent://contract-bot" },
                    "active", now, now, 1, "agent://contract-bot", "Started"),
                new List<ThreadEvent>
                {
                    new(Guid.NewGuid(), now, "agent://contract-bot",
                        "ThreadStarted", "Info", "Started"),
                }));

        var response = await _client.GetAsync("/api/v1/tenant/threads/contract-conv-detail", ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var body = await response.Content.ReadAsStringAsync(ct);
        OpenApiContract.AssertResponse(
            "/api/v1/tenant/threads/{id}", "get", "200", body);
    }

    [Fact]
    public async Task GetThread_NotFound_MatchesProblemDetailsContract()
    {
        var ct = TestContext.Current.CancellationToken;
        _factory.ThreadQueryService.ClearSubstitute();
        _factory.ThreadQueryService
            .GetAsync("contract-conv-missing", Arg.Any<CancellationToken>())
            .Returns((ThreadDetail?)null);

        var response = await _client.GetAsync("/api/v1/tenant/threads/contract-conv-missing", ct);
        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);

        var body = await response.Content.ReadAsStringAsync(ct);
        OpenApiContract.AssertResponse(
            "/api/v1/tenant/threads/{id}", "get", "404", body, "application/problem+json");
    }

    [Fact]
    public async Task PostThreadMessage_HappyPath_MatchesContract()
    {
        var ct = TestContext.Current.CancellationToken;
        _factory.MessageRouter.ClearSubstitute();

        // #1254 fixed the openapi.json shape so ThreadMessageResponse.responsePayload
        // is now a bare `$ref` to JsonElement, which matches null and any other
        // JSON value. Earlier revisions of this test had to force a non-null
        // reply because the broken `oneOf:[null, JsonElement]` wrapper rejected
        // null instances; the workaround is no longer needed. The structured
        // reply still exercises the JSON-object wire shape that real receivers
        // produce.
        var reply = new Message(
            Guid.NewGuid(),
            new Address("agent", Agent_ContractBot_Id),
            new Address("human", Human_LocalDevUser_Id),
            MessageType.Domain,
            "contract-conv-post",
            System.Text.Json.JsonSerializer.SerializeToElement(new { ack = "received" }),
            DateTimeOffset.UtcNow);
        _factory.MessageRouter
            .RouteAsync(Arg.Any<Message>(), Arg.Any<CancellationToken>())
            .Returns(Result<Message?, RoutingError>.Success(reply));

        var body = new ThreadMessageRequest(
            new AddressDto("agent", Agent_ContractBot_Id.ToString("N")),
            "Hello from contract test");

        var response = await _client.PostAsJsonAsync(
            "/api/v1/tenant/threads/contract-conv-post/messages", body, ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var responseBody = await response.Content.ReadAsStringAsync(ct);
        OpenApiContract.AssertResponse(
            "/api/v1/tenant/threads/{id}/messages", "post", "200", responseBody);
    }

    /// <summary>
    /// Round-trip contract test for the <c>kind</c> discriminator (#1421).
    /// Verifies that the accepted <c>kind</c> value is echoed back on the response
    /// for each valid kind value (information, question, answer, error).
    /// </summary>
    [Theory]
    [InlineData("information")]
    [InlineData("question")]
    [InlineData("answer")]
    [InlineData("error")]
    public async Task PostThreadMessage_KindRoundTrip_EchoesKindOnResponse(string kind)
    {
        var ct = TestContext.Current.CancellationToken;
        _factory.MessageRouter.ClearSubstitute();

        var reply = new Message(
            Guid.NewGuid(),
            new Address("agent", Agent_ContractBot_Id),
            new Address("human", Human_LocalDevUser_Id),
            MessageType.Domain,
            $"contract-conv-kind-{kind}",
            System.Text.Json.JsonSerializer.SerializeToElement(new { ack = "received" }),
            DateTimeOffset.UtcNow);
        _factory.MessageRouter
            .RouteAsync(Arg.Any<Message>(), Arg.Any<CancellationToken>())
            .Returns(Result<Message?, RoutingError>.Success(reply));

        var body = new ThreadMessageRequest(
            new AddressDto("agent", Agent_ContractBot_Id.ToString("N")),
            $"Test message for kind={kind}",
            kind);

        var response = await _client.PostAsJsonAsync(
            $"/api/v1/tenant/threads/contract-conv-kind-{kind}/messages", body, ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var responseBody = await response.Content.ReadAsStringAsync(ct);
        OpenApiContract.AssertResponse(
            "/api/v1/tenant/threads/{id}/messages", "post", "200", responseBody);

        // Verify the kind is echoed back on the response body.
        var json = System.Text.Json.JsonDocument.Parse(responseBody);
        json.RootElement.GetProperty("kind").GetString().ShouldBe(kind);
    }

    [Fact]
    public async Task PostThreadMessage_KindOmitted_DefaultsToInformation()
    {
        var ct = TestContext.Current.CancellationToken;
        _factory.MessageRouter.ClearSubstitute();

        var reply = new Message(
            Guid.NewGuid(),
            new Address("agent", Agent_ContractBot_Id),
            new Address("human", Human_LocalDevUser_Id),
            MessageType.Domain,
            "contract-conv-kind-default",
            System.Text.Json.JsonSerializer.SerializeToElement(new { ack = "received" }),
            DateTimeOffset.UtcNow);
        _factory.MessageRouter
            .RouteAsync(Arg.Any<Message>(), Arg.Any<CancellationToken>())
            .Returns(Result<Message?, RoutingError>.Success(reply));

        // Omit kind — server must default to "information".
        var body = new ThreadMessageRequest(
            new AddressDto("agent", Agent_ContractBot_Id.ToString("N")),
            "Test message with no kind");

        var response = await _client.PostAsJsonAsync(
            "/api/v1/tenant/threads/contract-conv-kind-default/messages", body, ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var responseBody = await response.Content.ReadAsStringAsync(ct);
        OpenApiContract.AssertResponse(
            "/api/v1/tenant/threads/{id}/messages", "post", "200", responseBody);

        var json = System.Text.Json.JsonDocument.Parse(responseBody);
        json.RootElement.GetProperty("kind").GetString().ShouldBe("information");
    }

    [Fact]
    public async Task CloseThread_NotFound_MatchesProblemDetailsContract()
    {
        var ct = TestContext.Current.CancellationToken;
        _factory.ThreadQueryService.ClearSubstitute();
        _factory.ThreadQueryService
            .GetAsync("contract-close-missing", Arg.Any<CancellationToken>())
            .Returns((ThreadDetail?)null);

        var body = new CloseThreadRequest("contract test");
        var response = await _client.PostAsJsonAsync(
            "/api/v1/tenant/threads/contract-close-missing/close", body, ct);
        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);

        var responseBody = await response.Content.ReadAsStringAsync(ct);
        OpenApiContract.AssertResponse(
            "/api/v1/tenant/threads/{id}/close", "post", "404",
            responseBody, "application/problem+json");
    }

    [Fact]
    public async Task CloseThread_HappyPath_MatchesContract()
    {
        var ct = TestContext.Current.CancellationToken;
        var now = DateTimeOffset.UtcNow;
        var contractBot = $"agent://{Agent_ContractBot_Id:N}";
        var summary = new ThreadSummary(
            "contract-close-ok",
            new[] { contractBot },
            "active", now, now, 1, contractBot, "Started");
        var beforeDetail = new ThreadDetail(summary, new List<ThreadEvent>());
        var afterDetail = new ThreadDetail(
            summary with { Status = "closed" },
            new List<ThreadEvent>
            {
                new(Guid.NewGuid(), now, contractBot,
                    "ThreadClosed", "Info", "Closed"),
            });

        _factory.ThreadQueryService.ClearSubstitute();
        _factory.ThreadQueryService
            .GetAsync("contract-close-ok", Arg.Any<CancellationToken>())
            .Returns(beforeDetail, afterDetail);

        var entry = new DirectoryEntry(
            new Address("agent", Agent_ContractBot_Id),
            ActorId: ActorContractBot_Id,
            DisplayName: "Bot",
            Description: "",
            Role: null,
            RegisteredAt: now);
        _factory.DirectoryService.ClearSubstitute();
        _factory.DirectoryService
            .ResolveAsync(
                Arg.Is<Address>(a => a.Scheme == "agent" && a.Id == Agent_ContractBot_Id),
                Arg.Any<CancellationToken>())
            .Returns(entry);

        var agentProxy = Substitute.For<IAgentActor>();
        _factory.ActorProxyFactory.ClearSubstitute();
        _factory.ActorProxyFactory
            .CreateActorProxy<IAgentActor>(
                Arg.Is<ActorId>(id => id.GetId() == ActorContractBot_Id.ToString("N")),
                nameof(AgentActor))
            .Returns(agentProxy);

        var body = new CloseThreadRequest("contract test");
        var response = await _client.PostAsJsonAsync(
            "/api/v1/tenant/threads/contract-close-ok/close", body, ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var responseBody = await response.Content.ReadAsStringAsync(ct);
        OpenApiContract.AssertResponse(
            "/api/v1/tenant/threads/{id}/close", "post", "200", responseBody);
    }

    /// <summary>
    /// Custom factory that swaps the conversation query service and message
    /// router for substitutes — mirrors ThreadEndpointsTests.Factory</c>.
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