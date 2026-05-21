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

public class OrchestrationCallbackEndpointsTests
{
    private static readonly Address UnitAddress =
        new(Address.UnitScheme, Guid.Parse("bbbbbbbb-0000-0000-0000-000000000001"));

    private static readonly Address ChildAddress =
        new(Address.AgentScheme, Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001"));

    private static readonly Address OtherChildAddress =
        new(Address.AgentScheme, Guid.Parse("aaaaaaaa-0000-0000-0000-000000000002"));

    [Fact]
    public async Task DelegateTo_AgentCallerWithAnyTarget_Returns200()
    {
        // ADR-0039 §3 (2026-05-19 amendment, #2536): an agent caller can
        // delegate to any addressable target in the same tenant — no
        // membership gate.
        using var factory = new OrchestrationCallbackTestHost();
        var caller = new Address(Address.AgentScheme, Guid.Parse("dddddddd-0000-0000-0000-000000000001"));
        var agent = Substitute.For<IAgent>();
        agent.ReceiveAsync(Arg.Any<Message>(), Arg.Any<CancellationToken>())
            .Returns((Message?)null);

        factory.RegisterAgent(ChildAddress, agent);
        var client = factory.CreateCallbackClient(caller);

        var response = await client.PostAsJsonAsync(
            "/v1/runtime/orchestration/delegate-to",
            DelegateRequest(caller, ChildAddress, factory.ThreadId),
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task DelegateTo_UnitCaller_AnyTarget_Returns200()
    {
        // ADR-0039 §3 (2026-05-19 amendment, #2536): unit callers can target
        // any addressable entity, not just direct members.
        using var factory = new OrchestrationCallbackTestHost();
        var caller = new Address(Address.AgentScheme, Guid.Parse("dddddddd-0000-0000-0000-000000000002"));
        var agent = Substitute.For<IAgent>();
        agent.ReceiveAsync(Arg.Any<Message>(), Arg.Any<CancellationToken>())
            .Returns((Message?)null);

        factory.RegisterAgent(ChildAddress, agent);
        var client = factory.CreateCallbackClient(caller);

        var response = await client.PostAsJsonAsync(
            "/v1/runtime/orchestration/delegate-to",
            DelegateRequest(caller, ChildAddress, factory.ThreadId),
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task DelegateTo_UnsupportedCallerScheme_Returns403()
    {
        using var factory = new OrchestrationCallbackTestHost();
        var caller = new Address(Address.HumanScheme, Guid.Parse("cccccccc-0000-0000-0000-000000000001"));
        var client = factory.CreateCallbackClient(caller);

        var response = await client.PostAsJsonAsync(
            "/v1/runtime/orchestration/delegate-to",
            DelegateRequest(caller, ChildAddress, factory.ThreadId),
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        var json = await ReadJsonAsync(response);
        json.GetProperty("error").GetString().ShouldBe("UnsupportedCallerScheme");
    }

    [Fact]
    public async Task DelegateTo_SelfTarget_Returns400()
    {
        using var factory = new OrchestrationCallbackTestHost();
        var client = factory.CreateCallbackClient(UnitAddress);

        var response = await client.PostAsJsonAsync(
            "/v1/runtime/orchestration/delegate-to",
            DelegateRequest(UnitAddress, UnitAddress, factory.ThreadId),
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var json = await ReadJsonAsync(response);
        json.GetProperty("error").GetString()
            .ShouldBe(OrchestrationException.RejectCodes.OrchestrationSelfDelegation);
    }

    [Fact]
    public async Task DelegateTo_HappyPath_ReturnsDeliveryAck()
    {
        // ADR-0049 — delegate_to returns a delivery acknowledgement, never
        // the target's response.
        using var factory = new OrchestrationCallbackTestHost();
        var agent = Substitute.For<IAgent>();
        agent.ReceiveAsync(Arg.Any<Message>(), Arg.Any<CancellationToken>())
            .Returns((Message?)null);

        factory.RegisterAgent(ChildAddress, agent);
        var client = factory.CreateCallbackClient(UnitAddress);

        var response = await client.PostAsJsonAsync(
            "/v1/runtime/orchestration/delegate-to",
            DelegateRequest(UnitAddress, ChildAddress, factory.ThreadId),
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var json = await ReadJsonAsync(response);
        json.GetProperty("delivered").GetBoolean().ShouldBeTrue();
        json.GetProperty("target").GetString().ShouldBe(ChildAddress.ToString());
        json.TryGetProperty("messageId", out _).ShouldBeTrue();
    }

    [Fact]
    public async Task FanoutTo_HappyPath_ReturnsPerTargetDeliveryOutcomes()
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
            "/v1/runtime/orchestration/fanout-to",
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
            "/v1/runtime/orchestration/delegate-to",
            DelegateRequest(ChildAddress, OtherChildAddress, factory.ThreadId),
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
            "/v1/runtime/orchestration/delegate-to",
            DelegateRequest(UnitAddress, ChildAddress, Guid.Parse("eeeeeeee-0000-0000-0000-000000000099")),
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
            "/v1/runtime/orchestration/delegate-to",
            DelegateRequest(UnitAddress, ChildAddress, factory.ThreadId),
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
            "/v1/runtime/orchestration/delegate-to",
            DelegateRequest(UnitAddress, ChildAddress, factory.ThreadId),
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        var json = await ReadJsonAsync(response);
        json.GetProperty("error").GetString().ShouldBe("InvalidToken");
    }

    // ---- #2582: callback-token rejection diagnostics --------------------

    [Fact]
    public async Task DelegateTo_ExpiredToken_EmitsErrorOccurredActivity()
    {
        using var factory = new OrchestrationCallbackTestHost();
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", factory.IssueExpiredToken(UnitAddress));

        var response = await client.PostAsJsonAsync(
            "/v1/runtime/orchestration/delegate-to",
            DelegateRequest(UnitAddress, ChildAddress, factory.ThreadId),
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
    public async Task DelegateTo_MalformedToken_EmitsErrorOccurredActivity()
    {
        using var factory = new OrchestrationCallbackTestHost();
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", "not-a-jwt");

        var response = await client.PostAsJsonAsync(
            "/v1/runtime/orchestration/delegate-to",
            DelegateRequest(UnitAddress, ChildAddress, factory.ThreadId),
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
    public async Task DelegateTo_HappyPath_EmitsNoErrorOccurredActivity()
    {
        // A legitimate orchestration call must not emit a rejection
        // ErrorOccurred activity (only the DecisionMade from the handler).
        using var factory = new OrchestrationCallbackTestHost();
        var agent = Substitute.For<IAgent>();
        agent.ReceiveAsync(Arg.Any<Message>(), Arg.Any<CancellationToken>())
            .Returns((Message?)null);
        factory.RegisterAgent(ChildAddress, agent);
        var client = factory.CreateCallbackClient(UnitAddress);

        var response = await client.PostAsJsonAsync(
            "/v1/runtime/orchestration/delegate-to",
            DelegateRequest(UnitAddress, ChildAddress, factory.ThreadId),
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        factory.CapturedActivities
            .ShouldNotContain(e => e.EventType == ActivityEventType.ErrorOccurred);
    }

    private static object DelegateRequest(Address caller, Address target, Guid threadId) =>
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
