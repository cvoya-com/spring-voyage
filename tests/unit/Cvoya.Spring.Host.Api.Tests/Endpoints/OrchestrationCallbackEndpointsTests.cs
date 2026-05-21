// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Api.Tests.Endpoints;

using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

using Cvoya.Spring.Core.Capabilities;
using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Dapr.Actors;
using Cvoya.Spring.Dapr.Auth;
using Cvoya.Spring.Dapr.Orchestration;

using NSubstitute;

using Shouldly;

using Xunit;

/// <summary>
/// REST-route coverage for the messaging callback endpoints
/// (<c>/messaging/send</c>, <c>/messaging/broadcast</c>) — ADR-0048 /
/// ADR-0049. Both are one-way delivery tools: the response is a delivery
/// acknowledgement, never the recipient's reply.
/// </summary>
public class OrchestrationCallbackEndpointsTests
{
    private const string SendRoute = "/v1/runtime/orchestration/messaging/send";
    private const string BroadcastRoute = "/v1/runtime/orchestration/messaging/broadcast";

    private static readonly Address UnitAddress =
        new(Address.UnitScheme, Guid.Parse("bbbbbbbb-0000-0000-0000-000000000001"));

    private static readonly Address ChildAddress =
        new(Address.AgentScheme, Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001"));

    private static readonly Address OtherChildAddress =
        new(Address.AgentScheme, Guid.Parse("aaaaaaaa-0000-0000-0000-000000000002"));

    [Fact]
    public async Task Send_AgentCallerWithAnyTarget_Returns200()
    {
        // ADR-0048 / ADR-0049: an agent caller can deliver a message to any
        // addressable target in the same tenant — no membership gate.
        using var factory = new OrchestrationCallbackTestHost();
        var caller = new Address(Address.AgentScheme, Guid.Parse("dddddddd-0000-0000-0000-000000000001"));
        var agent = Substitute.For<IAgent>();
        agent.ReceiveAsync(Arg.Any<Message>(), Arg.Any<CancellationToken>())
            .Returns((Message?)null);

        factory.RegisterAgent(ChildAddress, agent);
        var client = factory.CreateCallbackClient(caller);

        var response = await client.PostAsJsonAsync(
            SendRoute,
            SendRequest(caller, ChildAddress, factory.ThreadId),
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Send_UnitCaller_AnyTarget_Returns200()
    {
        using var factory = new OrchestrationCallbackTestHost();
        var caller = new Address(Address.AgentScheme, Guid.Parse("dddddddd-0000-0000-0000-000000000002"));
        var agent = Substitute.For<IAgent>();
        agent.ReceiveAsync(Arg.Any<Message>(), Arg.Any<CancellationToken>())
            .Returns((Message?)null);

        factory.RegisterAgent(ChildAddress, agent);
        var client = factory.CreateCallbackClient(caller);

        var response = await client.PostAsJsonAsync(
            SendRoute,
            SendRequest(caller, ChildAddress, factory.ThreadId),
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Send_UnsupportedCallerScheme_Returns403()
    {
        using var factory = new OrchestrationCallbackTestHost();
        var caller = new Address(Address.HumanScheme, Guid.Parse("cccccccc-0000-0000-0000-000000000001"));
        var client = factory.CreateCallbackClient(caller);

        var response = await client.PostAsJsonAsync(
            SendRoute,
            SendRequest(caller, ChildAddress, factory.ThreadId),
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        var json = await ReadJsonAsync(response);
        json.GetProperty("error").GetString().ShouldBe("UnsupportedCallerScheme");
    }

    [Fact]
    public async Task Send_SelfTarget_Returns400()
    {
        using var factory = new OrchestrationCallbackTestHost();
        var client = factory.CreateCallbackClient(UnitAddress);

        var response = await client.PostAsJsonAsync(
            SendRoute,
            SendRequest(UnitAddress, UnitAddress, factory.ThreadId),
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var json = await ReadJsonAsync(response);
        json.GetProperty("error").GetString()
            .ShouldBe(OrchestrationException.RejectCodes.OrchestrationSelfDelegation);
    }

    [Fact]
    public async Task Send_HappyPath_ReturnsDeliveryAck()
    {
        // ADR-0049 — sv.messaging.send returns a delivery acknowledgement,
        // never the target's response.
        using var factory = new OrchestrationCallbackTestHost();
        var agent = Substitute.For<IAgent>();
        agent.ReceiveAsync(Arg.Any<Message>(), Arg.Any<CancellationToken>())
            .Returns((Message?)null);

        factory.RegisterAgent(ChildAddress, agent);
        var client = factory.CreateCallbackClient(UnitAddress);

        var response = await client.PostAsJsonAsync(
            SendRoute,
            SendRequest(UnitAddress, ChildAddress, factory.ThreadId),
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var json = await ReadJsonAsync(response);
        json.GetProperty("delivered").GetBoolean().ShouldBeTrue();
        json.GetProperty("target").GetString().ShouldBe(ChildAddress.ToString());
        json.TryGetProperty("messageId", out _).ShouldBeTrue();
    }

    [Fact]
    public async Task Broadcast_HappyPath_ReturnsPerTargetDeliveryOutcomes()
    {
        using var factory = new OrchestrationCallbackTestHost();
        var firstAgent = Substitute.For<IAgent>();
        var secondAgent = Substitute.For<IAgent>();
        firstAgent.ReceiveAsync(Arg.Any<Message>(), Arg.Any<CancellationToken>())
            .Returns((Message?)null);
        secondAgent.ReceiveAsync(Arg.Any<Message>(), Arg.Any<CancellationToken>())
            .Returns((Message?)null);

        factory.RegisterAgent(ChildAddress, firstAgent);
        factory.RegisterAgent(OtherChildAddress, secondAgent);
        var client = factory.CreateCallbackClient(UnitAddress);

        var response = await client.PostAsJsonAsync(
            BroadcastRoute,
            new
            {
                callerAddress = UnitAddress.ToString(),
                targetAddresses = new[] { ChildAddress.ToString(), OtherChildAddress.ToString() },
                threadId = factory.ThreadId,
                messageId = Guid.NewGuid(),
                messageContent = "work",
                reason = "parallel work",
            },
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var json = await ReadJsonAsync(response);
        var deliveries = json.GetProperty("deliveries");
        deliveries.GetArrayLength().ShouldBe(2);
        deliveries[0].GetProperty("delivered").GetBoolean().ShouldBeTrue();
        deliveries[1].GetProperty("delivered").GetBoolean().ShouldBeTrue();
    }

    [Fact]
    public async Task AnyEndpoint_CallerAddressDiffersFromToken_Returns403()
    {
        using var factory = new OrchestrationCallbackTestHost();
        var client = factory.CreateCallbackClient(UnitAddress);

        var response = await client.PostAsJsonAsync(
            SendRoute,
            SendRequest(ChildAddress, OtherChildAddress, factory.ThreadId),
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        var json = await ReadJsonAsync(response);
        json.GetProperty("error").GetString().ShouldBe("CallerMismatch");
    }

    [Fact]
    public async Task AnyEndpoint_ThreadIdDiffersFromToken_Returns400()
    {
        using var factory = new OrchestrationCallbackTestHost();
        var client = factory.CreateCallbackClient(UnitAddress);

        var response = await client.PostAsJsonAsync(
            SendRoute,
            SendRequest(UnitAddress, ChildAddress, Guid.Parse("eeeeeeee-0000-0000-0000-000000000099")),
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var json = await ReadJsonAsync(response);
        json.GetProperty("error").GetString().ShouldBe("ThreadMismatch");
    }

    [Fact]
    public async Task AnyEndpoint_InvalidToken_Returns401()
    {
        using var factory = new OrchestrationCallbackTestHost();
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "not-a-jwt");

        var response = await client.PostAsJsonAsync(
            SendRoute,
            SendRequest(UnitAddress, ChildAddress, factory.ThreadId),
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        var json = await ReadJsonAsync(response);
        json.GetProperty("error").GetString().ShouldBe("InvalidToken");
    }

    [Fact]
    public async Task AnyEndpoint_MissingAuthHeader_Returns401()
    {
        using var factory = new OrchestrationCallbackTestHost();
        var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync(
            SendRoute,
            SendRequest(UnitAddress, ChildAddress, factory.ThreadId),
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        var json = await ReadJsonAsync(response);
        json.GetProperty("error").GetString().ShouldBe("InvalidToken");
    }

    [Fact]
    public async Task Send_EmitsMessageSentActivity()
    {
        // ADR-0048 / ADR-0049 — a successful send emits a plain MessageSent
        // activity, never a DecisionMade.
        using var factory = new OrchestrationCallbackTestHost();
        var agent = Substitute.For<IAgent>();
        agent.ReceiveAsync(Arg.Any<Message>(), Arg.Any<CancellationToken>())
            .Returns((Message?)null);
        factory.RegisterAgent(ChildAddress, agent);
        var client = factory.CreateCallbackClient(UnitAddress);

        var response = await client.PostAsJsonAsync(
            SendRoute,
            SendRequest(UnitAddress, ChildAddress, factory.ThreadId),
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        factory.CapturedActivities
            .ShouldContain(e => e.EventType == ActivityEventType.MessageSent);
        factory.CapturedActivities
            .ShouldNotContain(e => e.EventType == ActivityEventType.DecisionMade);
    }

    // ---- #2582: callback-token rejection diagnostics --------------------

    [Fact]
    public async Task Send_ExpiredToken_EmitsErrorOccurredActivity()
    {
        using var factory = new OrchestrationCallbackTestHost();
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", factory.IssueExpiredToken(UnitAddress));

        var response = await client.PostAsJsonAsync(
            SendRoute,
            SendRequest(UnitAddress, ChildAddress, factory.ThreadId),
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        // Exactly one ErrorOccurred activity, naming the subject and the
        // structured rejection reason (Expired).
        var emitted = factory.CapturedActivities
            .Where(e => e.EventType == ActivityEventType.ErrorOccurred)
            .ToList();
        emitted.Count.ShouldBe(1);
        var activity = emitted[0];
        activity.Severity.ShouldBe(ActivitySeverity.Warning);
        activity.Source.ShouldBe(UnitAddress);
        activity.Details!.Value.GetProperty("reason").GetString()
            .ShouldBe(CallbackTokenValidationReason.Expired.ToString());
    }

    [Fact]
    public async Task Send_MalformedToken_EmitsErrorOccurredActivity()
    {
        using var factory = new OrchestrationCallbackTestHost();
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", "not-a-jwt");

        var response = await client.PostAsJsonAsync(
            SendRoute,
            SendRequest(UnitAddress, ChildAddress, factory.ThreadId),
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        var emitted = factory.CapturedActivities
            .Where(e => e.EventType == ActivityEventType.ErrorOccurred)
            .ToList();
        emitted.Count.ShouldBe(1);
        emitted[0].Details!.Value.GetProperty("reason").GetString()
            .ShouldBe(CallbackTokenValidationReason.Malformed.ToString());
    }

    [Fact]
    public async Task Send_HappyPath_EmitsNoErrorOccurredActivity()
    {
        using var factory = new OrchestrationCallbackTestHost();
        var agent = Substitute.For<IAgent>();
        agent.ReceiveAsync(Arg.Any<Message>(), Arg.Any<CancellationToken>())
            .Returns((Message?)null);
        factory.RegisterAgent(ChildAddress, agent);
        var client = factory.CreateCallbackClient(UnitAddress);

        var response = await client.PostAsJsonAsync(
            SendRoute,
            SendRequest(UnitAddress, ChildAddress, factory.ThreadId),
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        factory.CapturedActivities
            .ShouldNotContain(e => e.EventType == ActivityEventType.ErrorOccurred);
    }

    private static object SendRequest(Address caller, Address target, Guid threadId) =>
        new
        {
            callerAddress = caller.ToString(),
            targetAddress = target.ToString(),
            threadId,
            messageId = Guid.NewGuid(),
            messageContent = "work",
            reason = "because",
        };

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
}
