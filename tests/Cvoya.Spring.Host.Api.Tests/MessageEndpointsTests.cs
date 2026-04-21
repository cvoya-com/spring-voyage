// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Api.Tests;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using Cvoya.Spring.Core.Directory;
using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Dapr.Actors;
using Cvoya.Spring.Host.Api.Auth;
using Cvoya.Spring.Host.Api.Models;

using NSubstitute;

using Shouldly;

using Xunit;

public class MessageEndpointsTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public MessageEndpointsTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task SendMessage_WhenAddressNotFound_Returns404()
    {
        var ct = TestContext.Current.CancellationToken;

        // Directory returns null for this address, so routing fails with ADDRESS_NOT_FOUND.
        _factory.DirectoryService.ResolveAsync(
            Arg.Is<Address>(a => a.Scheme == "agent" && a.Path == "unknown-agent"),
            Arg.Any<CancellationToken>())
            .Returns((DirectoryEntry?)null);

        var request = new SendMessageRequest(
            new AddressDto("agent", "unknown-agent"),
            "Domain",
            "conv-1",
            JsonSerializer.SerializeToElement(new { Text = "hello" }));

        var response = await _client.PostAsJsonAsync("/api/v1/messages", request, ct);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task SendMessage_WhenInvalidType_Returns400()
    {
        var ct = TestContext.Current.CancellationToken;

        var request = new SendMessageRequest(
            new AddressDto("agent", "test-agent"),
            "InvalidType",
            null,
            JsonSerializer.SerializeToElement(new { Text = "hello" }));

        var response = await _client.PostAsJsonAsync("/api/v1/messages", request, ct);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task SendMessage_UsesAuthenticatedCallerAsFromAddress()
    {
        // #339: the endpoint must thread the authenticated subject through
        // as the Message.From so MessageRouter's permission gate evaluates
        // against the real caller rather than a synthetic human://api. In
        // LocalDev mode (used by the test factory) the LocalDevAuthHandler
        // surfaces 'local-dev-user' as the NameIdentifier — that's what the
        // caller accessor should pick up.
        var ct = TestContext.Current.CancellationToken;

        var entry = new DirectoryEntry(
            new Address("agent", "test-agent"),
            "actor-1",
            "Test Agent",
            "A test agent",
            null,
            DateTimeOffset.UtcNow);
        _factory.DirectoryService
            .ResolveAsync(Arg.Is<Address>(a => a.Scheme == "agent" && a.Path == "test-agent"),
                Arg.Any<CancellationToken>())
            .Returns(entry);

        var agent = Substitute.For<IAgent>();
        Message? observed = null;
        agent.ReceiveAsync(Arg.Do<Message>(m => observed = m), Arg.Any<CancellationToken>())
            .Returns((Message?)null);
        _factory.AgentProxyResolver
            .Resolve(Arg.Is<string>(s => string.Equals(s, "agent", StringComparison.OrdinalIgnoreCase)),
                "actor-1")
            .Returns(agent);

        var request = new SendMessageRequest(
            new AddressDto("agent", "test-agent"),
            "Domain",
            "conv-1",
            JsonSerializer.SerializeToElement(new { Text = "hello" }));

        var response = await _client.PostAsJsonAsync("/api/v1/messages", request, ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        observed.ShouldNotBeNull();
        observed!.From.Scheme.ShouldBe("human");
        // CustomWebApplicationFactory forces LocalDev=true, so the configured
        // NameIdentifier is 'local-dev-user'.
        observed.From.Path.ShouldBe(Cvoya.Spring.Host.Api.Auth.AuthConstants.DefaultLocalUserId);
        // And it must NOT be the pre-#339 synthetic 'api' identity.
        observed.From.Path.ShouldNotBe(AuthenticatedCallerAccessor.FallbackHumanId);
    }

    [Fact]
    public async Task SendMessage_DomainToAgentWithoutConversationId_AutoGeneratesAndReturns()
    {
        // #985: AgentActor hard-requires a ConversationId on Domain messages
        // and surfaces its exception as a raw 502 when missing. The schema
        // marks the field optional, so the endpoint auto-generates a fresh
        // UUID for Domain sends to agent:// targets when the caller omits
        // one, and echoes the resolved id in the response so the operator
        // can thread follow-up sends under the same conversation.
        var ct = TestContext.Current.CancellationToken;

        var entry = new DirectoryEntry(
            new Address("agent", "conv-agent"),
            "actor-conv",
            "Conv Agent",
            "A test agent",
            null,
            DateTimeOffset.UtcNow);
        _factory.DirectoryService
            .ResolveAsync(Arg.Is<Address>(a => a.Scheme == "agent" && a.Path == "conv-agent"),
                Arg.Any<CancellationToken>())
            .Returns(entry);

        var agent = Substitute.For<IAgent>();
        Message? observed = null;
        agent.ReceiveAsync(Arg.Do<Message>(m => observed = m), Arg.Any<CancellationToken>())
            .Returns((Message?)null);
        _factory.AgentProxyResolver
            .Resolve(Arg.Is<string>(s => string.Equals(s, "agent", StringComparison.OrdinalIgnoreCase)),
                "actor-conv")
            .Returns(agent);

        var request = new SendMessageRequest(
            new AddressDto("agent", "conv-agent"),
            "Domain",
            null,
            JsonSerializer.SerializeToElement(new { Text = "hello" }));

        var response = await _client.PostAsJsonAsync("/api/v1/messages", request, ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<MessageResponse>(cancellationToken: ct);
        body.ShouldNotBeNull();
        body!.ConversationId.ShouldNotBeNullOrWhiteSpace();
        Guid.TryParse(body.ConversationId, out _).ShouldBeTrue();

        observed.ShouldNotBeNull();
        // Same id must thread through to the actor call so AgentActor's
        // ConversationId guard is satisfied.
        observed!.ConversationId.ShouldBe(body.ConversationId);
    }

    [Fact]
    public async Task SendMessage_DomainToAgentWithConversationId_IsPassedThrough()
    {
        // Caller-supplied conversation ids must pass through untouched so
        // existing clients that thread under a known id keep working.
        var ct = TestContext.Current.CancellationToken;

        var entry = new DirectoryEntry(
            new Address("agent", "passthrough-agent"),
            "actor-pass",
            "Passthrough Agent",
            "A test agent",
            null,
            DateTimeOffset.UtcNow);
        _factory.DirectoryService
            .ResolveAsync(Arg.Is<Address>(a => a.Scheme == "agent" && a.Path == "passthrough-agent"),
                Arg.Any<CancellationToken>())
            .Returns(entry);

        var agent = Substitute.For<IAgent>();
        Message? observed = null;
        agent.ReceiveAsync(Arg.Do<Message>(m => observed = m), Arg.Any<CancellationToken>())
            .Returns((Message?)null);
        _factory.AgentProxyResolver
            .Resolve(Arg.Is<string>(s => string.Equals(s, "agent", StringComparison.OrdinalIgnoreCase)),
                "actor-pass")
            .Returns(agent);

        const string suppliedId = "caller-supplied-conversation-1";
        var request = new SendMessageRequest(
            new AddressDto("agent", "passthrough-agent"),
            "Domain",
            suppliedId,
            JsonSerializer.SerializeToElement(new { Text = "hello" }));

        var response = await _client.PostAsJsonAsync("/api/v1/messages", request, ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<MessageResponse>(cancellationToken: ct);
        body.ShouldNotBeNull();
        body!.ConversationId.ShouldBe(suppliedId);
        observed.ShouldNotBeNull();
        observed!.ConversationId.ShouldBe(suppliedId);
    }

    [Fact]
    public async Task SendMessage_DomainToUnitWithoutConversationId_DoesNotAutoGenerate()
    {
        // The auto-gen is scoped to agent:// targets — unit:// routing goes
        // through UnitActor which has its own conversation-opening behaviour
        // and must not be short-circuited here.
        var ct = TestContext.Current.CancellationToken;

        var entry = new DirectoryEntry(
            new Address("unit", "engineering-team"),
            "unit-1",
            "Engineering",
            "Team",
            null,
            DateTimeOffset.UtcNow);
        _factory.DirectoryService
            .ResolveAsync(Arg.Is<Address>(a => a.Scheme == "unit" && a.Path == "engineering-team"),
                Arg.Any<CancellationToken>())
            .Returns(entry);

        // Human-to-unit routing runs a Viewer permission check in
        // MessageRouter; grant it on the mocked permission service so the
        // message reaches the actor rather than bouncing at the gate.
        var permissionService = (Cvoya.Spring.Dapr.Auth.IPermissionService)_factory.Services
            .GetService(typeof(Cvoya.Spring.Dapr.Auth.IPermissionService))!;
        permissionService.ResolveEffectivePermissionAsync(
                Arg.Any<string>(), "unit-1", Arg.Any<CancellationToken>())
            .Returns(Cvoya.Spring.Dapr.Actors.PermissionLevel.Viewer);

        var unit = Substitute.For<IAgent>();
        Message? observed = null;
        unit.ReceiveAsync(Arg.Do<Message>(m => observed = m), Arg.Any<CancellationToken>())
            .Returns((Message?)null);
        _factory.AgentProxyResolver
            .Resolve(Arg.Is<string>(s => string.Equals(s, "unit", StringComparison.OrdinalIgnoreCase)),
                "unit-1")
            .Returns(unit);

        var request = new SendMessageRequest(
            new AddressDto("unit", "engineering-team"),
            "Domain",
            null,
            JsonSerializer.SerializeToElement(new { Text = "hello" }));

        var response = await _client.PostAsJsonAsync("/api/v1/messages", request, ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<MessageResponse>(cancellationToken: ct);
        body.ShouldNotBeNull();
        body!.ConversationId.ShouldBeNull();
        observed.ShouldNotBeNull();
        observed!.ConversationId.ShouldBeNull();
    }

    [Fact]
    public async Task SendMessage_NonDomainTypeToAgentWithoutConversationId_DoesNotAutoGenerate()
    {
        // Control messages (HealthCheck, Cancel, StatusQuery, ...) don't need
        // a conversation id — keep them untouched so the auto-gen is strictly
        // scoped to the Domain path surfaced by #985.
        var ct = TestContext.Current.CancellationToken;

        var entry = new DirectoryEntry(
            new Address("agent", "ping-agent"),
            "actor-ping",
            "Ping Agent",
            "A test agent",
            null,
            DateTimeOffset.UtcNow);
        _factory.DirectoryService
            .ResolveAsync(Arg.Is<Address>(a => a.Scheme == "agent" && a.Path == "ping-agent"),
                Arg.Any<CancellationToken>())
            .Returns(entry);

        var agent = Substitute.For<IAgent>();
        Message? observed = null;
        agent.ReceiveAsync(Arg.Do<Message>(m => observed = m), Arg.Any<CancellationToken>())
            .Returns((Message?)null);
        _factory.AgentProxyResolver
            .Resolve(Arg.Is<string>(s => string.Equals(s, "agent", StringComparison.OrdinalIgnoreCase)),
                "actor-ping")
            .Returns(agent);

        var request = new SendMessageRequest(
            new AddressDto("agent", "ping-agent"),
            "HealthCheck",
            null,
            JsonSerializer.SerializeToElement(new { }));

        var response = await _client.PostAsJsonAsync("/api/v1/messages", request, ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<MessageResponse>(cancellationToken: ct);
        body.ShouldNotBeNull();
        body!.ConversationId.ShouldBeNull();
        observed.ShouldNotBeNull();
        observed!.ConversationId.ShouldBeNull();
    }
}