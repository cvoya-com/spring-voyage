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
    private static readonly Guid UnknownAgentId = new("11111111-0000-0000-0000-000000000099");
    private static readonly Guid TestAgentId = new("11111111-0000-0000-0000-000000000001");
    private static readonly Guid ConvAgentId = new("11111111-0000-0000-0000-000000000002");
    private static readonly Guid PassthroughAgentId = new("11111111-0000-0000-0000-000000000003");
    private static readonly Guid EngineeringTeamId = new("22222222-0000-0000-0000-000000000001");
    private static readonly Guid PingAgentId = new("11111111-0000-0000-0000-000000000004");
    private static readonly Guid ValidatingAgentId = new("11111111-0000-0000-0000-000000000005");
    private static readonly Guid StrictAgentId = new("11111111-0000-0000-0000-000000000006");
    private static readonly Guid RemotedAgentId = new("11111111-0000-0000-0000-000000000007");
    private static readonly Guid FlakyAgentId = new("11111111-0000-0000-0000-000000000008");
    private static readonly Guid NonGuidThreadAgentId = new("11111111-0000-0000-0000-000000000009");
    private static readonly Guid UnknownThreadAgentId = new("11111111-0000-0000-0000-00000000000a");

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
            Arg.Is<Address>(a => a.Scheme == "agent" && a.Id == UnknownAgentId),
            Arg.Any<CancellationToken>())
            .Returns((DirectoryEntry?)null);

        var request = new SendMessageRequest(
            new AddressDto("agent", UnknownAgentId.ToString("N")),
            "Domain",
            Guid.NewGuid().ToString(),
            JsonSerializer.SerializeToElement(new { Text = "hello" }));

        var response = await _client.PostAsJsonAsync("/api/v1/tenant/messages", request, ct);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task SendMessage_WhenInvalidType_Returns400()
    {
        var ct = TestContext.Current.CancellationToken;

        var request = new SendMessageRequest(
            new AddressDto("agent", TestAgentId.ToString("N")),
            "InvalidType",
            null,
            JsonSerializer.SerializeToElement(new { Text = "hello" }));

        var response = await _client.PostAsJsonAsync("/api/v1/tenant/messages", request, ct);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task SendMessage_UsesAuthenticatedCallerAsFromAddress()
    {
        // #339 / #2768: the endpoint must thread the authenticated subject
        // through as the Message.From so MessageRouter's permission gate
        // evaluates against the real caller. Per ADR-0047 §1 the caller
        // identity is a TenantUser; in OSS the LocalDevAuthHandler
        // resolves to the pinned operator id.
        var ct = TestContext.Current.CancellationToken;

        var entry = new DirectoryEntry(
            new Address("agent", TestAgentId),
            TestAgentId,
            "Test Agent",
            "A test agent",
            null,
            DateTimeOffset.UtcNow);
        _factory.DirectoryService
            .ResolveAsync(Arg.Is<Address>(a => a.Scheme == "agent" && a.Id == TestAgentId),
                Arg.Any<CancellationToken>())
            .Returns(entry);

        var agent = Substitute.For<IAgent>();
        Message? observed = null;
        agent.ReceiveAsync(Arg.Do<Message>(m => observed = m), Arg.Any<CancellationToken>())
            .Returns((Message?)null);
        _factory.AgentProxyResolver
            .Resolve(Arg.Is<string>(s => string.Equals(s, "agent", StringComparison.OrdinalIgnoreCase)),
                TestAgentId.ToString("N"))
            .Returns(agent);

        var request = new SendMessageRequest(
            new AddressDto("agent", TestAgentId.ToString("N")),
            "Domain",
            Guid.NewGuid().ToString(),
            JsonSerializer.SerializeToElement(new { Text = "hello" }));

        var response = await _client.PostAsJsonAsync("/api/v1/tenant/messages", request, ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        observed.ShouldNotBeNull();
        observed!.From.Scheme.ShouldBe(Address.TenantUserScheme);
        observed.From.Id.ShouldBe(Cvoya.Spring.Core.Tenancy.OssTenantUserIds.Operator);
    }

    [Fact]
    public async Task SendMessage_DomainToAgentWithoutThreadId_AutoGeneratesAndReturns()
    {
        // #985: AgentActor hard-requires a ThreadId on Domain messages
        // and surfaces its exception as a raw 502 when missing. The schema
        // marks the field optional, so the endpoint auto-generates a fresh
        // UUID for Domain sends to agent:// targets when the caller omits
        // one, and echoes the resolved id in the response so the operator
        // can thread follow-up sends under the same conversation.
        var ct = TestContext.Current.CancellationToken;

        var entry = new DirectoryEntry(
            new Address("agent", ConvAgentId),
            ConvAgentId,
            "Conv Agent",
            "A test agent",
            null,
            DateTimeOffset.UtcNow);
        _factory.DirectoryService
            .ResolveAsync(Arg.Is<Address>(a => a.Scheme == "agent" && a.Id == ConvAgentId),
                Arg.Any<CancellationToken>())
            .Returns(entry);

        var agent = Substitute.For<IAgent>();
        Message? observed = null;
        agent.ReceiveAsync(Arg.Do<Message>(m => observed = m), Arg.Any<CancellationToken>())
            .Returns((Message?)null);
        _factory.AgentProxyResolver
            .Resolve(Arg.Is<string>(s => string.Equals(s, "agent", StringComparison.OrdinalIgnoreCase)),
                ConvAgentId.ToString("N"))
            .Returns(agent);

        var request = new SendMessageRequest(
            new AddressDto("agent", ConvAgentId.ToString("N")),
            "Domain",
            null,
            JsonSerializer.SerializeToElement(new { Text = "hello" }));

        var response = await _client.PostAsJsonAsync("/api/v1/tenant/messages", request, ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<MessageResponse>(cancellationToken: ct);
        body.ShouldNotBeNull();
        body!.ThreadId.ShouldNotBeNullOrWhiteSpace();
        Guid.TryParse(body.ThreadId, out _).ShouldBeTrue();

        observed.ShouldNotBeNull();
        // Same id must thread through to the actor call so AgentActor's
        // ThreadId guard is satisfied.
        observed!.ThreadId.ShouldBe(body.ThreadId);
    }

    [Fact]
    public async Task SendMessage_DomainToAgentWithThreadId_IsPassedThrough()
    {
        // Caller-supplied conversation ids must pass through untouched so
        // existing clients that thread under a known id keep working.
        var ct = TestContext.Current.CancellationToken;

        var entry = new DirectoryEntry(
            new Address("agent", PassthroughAgentId),
            PassthroughAgentId,
            "Passthrough Agent",
            "A test agent",
            null,
            DateTimeOffset.UtcNow);
        _factory.DirectoryService
            .ResolveAsync(Arg.Is<Address>(a => a.Scheme == "agent" && a.Id == PassthroughAgentId),
                Arg.Any<CancellationToken>())
            .Returns(entry);

        var agent = Substitute.For<IAgent>();
        Message? observed = null;
        agent.ReceiveAsync(Arg.Do<Message>(m => observed = m), Arg.Any<CancellationToken>())
            .Returns((Message?)null);
        _factory.AgentProxyResolver
            .Resolve(Arg.Is<string>(s => string.Equals(s, "agent", StringComparison.OrdinalIgnoreCase)),
                PassthroughAgentId.ToString("N"))
            .Returns(agent);

        // #2047 / ADR-0030: thread ids are stable Guids. Lenient parsing
        // accepts both dashed and no-dash forms — using the dashed form
        // here exercises the canonical-Guid passthrough path.
        var suppliedId = Guid.NewGuid().ToString();
        var request = new SendMessageRequest(
            new AddressDto("agent", PassthroughAgentId.ToString("N")),
            "Domain",
            suppliedId,
            JsonSerializer.SerializeToElement(new { Text = "hello" }));

        var response = await _client.PostAsJsonAsync("/api/v1/tenant/messages", request, ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<MessageResponse>(cancellationToken: ct);
        body.ShouldNotBeNull();
        body!.ThreadId.ShouldBe(suppliedId);
        observed.ShouldNotBeNull();
        observed!.ThreadId.ShouldBe(suppliedId);
    }

    [Fact]
    public async Task SendMessage_DomainToUnitWithoutThreadId_AutoGeneratesViaRegistry()
    {
        // #2047 / ADR-0030: every Domain send (regardless of target scheme)
        // resolves a participant-set thread id through IThreadRegistry when
        // the caller omits one, so the activity-event CorrelationId is
        // populated for unit-routed conversations too. Closes #2034.
        var ct = TestContext.Current.CancellationToken;

        var entry = new DirectoryEntry(
            new Address("unit", EngineeringTeamId),
            EngineeringTeamId,
            "Engineering",
            "Team",
            null,
            DateTimeOffset.UtcNow);
        _factory.DirectoryService
            .ResolveAsync(Arg.Is<Address>(a => a.Scheme == "unit" && a.Id == EngineeringTeamId),
                Arg.Any<CancellationToken>())
            .Returns(entry);

        // Human-to-unit routing runs a Viewer permission check in
        // MessageRouter; grant it on the mocked permission service so the
        // message reaches the actor rather than bouncing at the gate.
        // #2768: the permission service now takes a typed Address caller and
        // a Guid unitId instead of two strings.
        var permissionService = (Cvoya.Spring.Dapr.Auth.IPermissionService)_factory.Services
            .GetService(typeof(Cvoya.Spring.Dapr.Auth.IPermissionService))!;
        permissionService.ResolveEffectivePermissionAsync(
                Arg.Any<Address>(), EngineeringTeamId, Arg.Any<CancellationToken>())
            .Returns(Cvoya.Spring.Dapr.Actors.PermissionLevel.Viewer);

        var unit = Substitute.For<IAgent>();
        Message? observed = null;
        unit.ReceiveAsync(Arg.Do<Message>(m => observed = m), Arg.Any<CancellationToken>())
            .Returns((Message?)null);
        _factory.AgentProxyResolver
            .Resolve(Arg.Is<string>(s => string.Equals(s, "unit", StringComparison.OrdinalIgnoreCase)),
                EngineeringTeamId.ToString("N"))
            .Returns(unit);

        var request = new SendMessageRequest(
            new AddressDto("unit", EngineeringTeamId.ToString("N")),
            "Domain",
            null,
            JsonSerializer.SerializeToElement(new { Text = "hello" }));

        var response = await _client.PostAsJsonAsync("/api/v1/tenant/messages", request, ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<MessageResponse>(cancellationToken: ct);
        body.ShouldNotBeNull();
        body!.ThreadId.ShouldNotBeNullOrWhiteSpace();
        Guid.TryParse(body.ThreadId, out _).ShouldBeTrue();

        observed.ShouldNotBeNull();
        observed!.ThreadId.ShouldBe(body.ThreadId);
    }

    [Fact]
    public async Task SendMessage_NonDomainTypeToAgentWithoutThreadId_DoesNotAutoGenerate()
    {
        // Control messages (HealthCheck, Cancel, StatusQuery, ...) don't need
        // a conversation id — keep them untouched so the auto-gen is strictly
        // scoped to the Domain path surfaced by #985.
        var ct = TestContext.Current.CancellationToken;

        var entry = new DirectoryEntry(
            new Address("agent", PingAgentId),
            PingAgentId,
            "Ping Agent",
            "A test agent",
            null,
            DateTimeOffset.UtcNow);
        _factory.DirectoryService
            .ResolveAsync(Arg.Is<Address>(a => a.Scheme == "agent" && a.Id == PingAgentId),
                Arg.Any<CancellationToken>())
            .Returns(entry);

        var agent = Substitute.For<IAgent>();
        Message? observed = null;
        agent.ReceiveAsync(Arg.Do<Message>(m => observed = m), Arg.Any<CancellationToken>())
            .Returns((Message?)null);
        _factory.AgentProxyResolver
            .Resolve(Arg.Is<string>(s => string.Equals(s, "agent", StringComparison.OrdinalIgnoreCase)),
                PingAgentId.ToString("N"))
            .Returns(agent);

        var request = new SendMessageRequest(
            new AddressDto("agent", PingAgentId.ToString("N")),
            "HealthCheck",
            null,
            JsonSerializer.SerializeToElement(new { }));

        var response = await _client.PostAsJsonAsync("/api/v1/tenant/messages", request, ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<MessageResponse>(cancellationToken: ct);
        body.ShouldNotBeNull();
        body!.ThreadId.ShouldBeNull();
        observed.ShouldNotBeNull();
        observed!.ThreadId.ShouldBeNull();
    }

    // #993: caller-side validation failures thrown inside the destination
    // actor used to surface as 502 (via RoutingError.DeliveryFailed). They
    // are now classified as 400 with a stable `code` extension so clients
    // can switch on it without parsing the detail. Genuine downstream
    // failures continue to map to 502.

    [Fact]
    public async Task SendMessage_WhenActorThrowsCallerValidation_Returns400WithCode()
    {
        var ct = TestContext.Current.CancellationToken;

        var entry = new DirectoryEntry(
            new Address("agent", ValidatingAgentId),
            ValidatingAgentId,
            "Validating Agent",
            "A test agent",
            null,
            DateTimeOffset.UtcNow);
        _factory.DirectoryService
            .ResolveAsync(Arg.Is<Address>(a => a.Scheme == "agent" && a.Id == ValidatingAgentId),
                Arg.Any<CancellationToken>())
            .Returns(entry);

        var agent = Substitute.For<IAgent>();
        agent.ReceiveAsync(Arg.Any<Message>(), Arg.Any<CancellationToken>())
            .Returns<Task<Message?>>(_ => throw new CallerValidationException(
                CallerValidationCodes.MissingThreadId,
                "Domain messages must have a ThreadId"));
        _factory.AgentProxyResolver
            .Resolve(Arg.Is<string>(s => string.Equals(s, "agent", StringComparison.OrdinalIgnoreCase)),
                ValidatingAgentId.ToString("N"))
            .Returns(agent);

        // Bypass the #985 auto-gen by using a non-agent target is not
        // needed — this agent would also be auto-populated; instead we
        // use the HealthCheck type (no auto-gen) and let the stubbed
        // actor throw for whatever reason. The endpoint must map 400.
        var request = new SendMessageRequest(
            new AddressDto("agent", ValidatingAgentId.ToString("N")),
            "HealthCheck",
            null,
            JsonSerializer.SerializeToElement(new { }));

        var response = await _client.PostAsJsonAsync("/api/v1/tenant/messages", request, ct);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        var problem = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ct);
        problem.GetProperty("status").GetInt32().ShouldBe(400);
        problem.GetProperty("detail").GetString().ShouldBe("Domain messages must have a ThreadId");
        problem.GetProperty("code").GetString().ShouldBe(CallerValidationCodes.MissingThreadId);
    }

    [Fact]
    public async Task SendMessage_WhenActorThrowsUnknownMessageType_Returns400WithUnknownTypeCode()
    {
        var ct = TestContext.Current.CancellationToken;

        var entry = new DirectoryEntry(
            new Address("agent", StrictAgentId),
            StrictAgentId,
            "Strict Agent",
            "A test agent",
            null,
            DateTimeOffset.UtcNow);
        _factory.DirectoryService
            .ResolveAsync(Arg.Is<Address>(a => a.Scheme == "agent" && a.Id == StrictAgentId),
                Arg.Any<CancellationToken>())
            .Returns(entry);

        var agent = Substitute.For<IAgent>();
        agent.ReceiveAsync(Arg.Any<Message>(), Arg.Any<CancellationToken>())
            .Returns<Task<Message?>>(_ => throw new CallerValidationException(
                CallerValidationCodes.UnknownMessageType,
                "Unknown message type: Amendment"));
        _factory.AgentProxyResolver
            .Resolve(Arg.Is<string>(s => string.Equals(s, "agent", StringComparison.OrdinalIgnoreCase)),
                StrictAgentId.ToString("N"))
            .Returns(agent);

        var request = new SendMessageRequest(
            new AddressDto("agent", StrictAgentId.ToString("N")),
            "Amendment",
            "conv-x",
            JsonSerializer.SerializeToElement(new { }));

        var response = await _client.PostAsJsonAsync("/api/v1/tenant/messages", request, ct);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ct);
        problem.GetProperty("code").GetString().ShouldBe(CallerValidationCodes.UnknownMessageType);
    }

    [Fact]
    public async Task SendMessage_WhenRemotingLosesExceptionType_StillReturns400()
    {
        // Dapr actor-remoting drops custom exception types — they arrive as
        // a generic ActorInvokeException whose Message preserves the original
        // text. CallerValidationException encodes its code into the message
        // as [caller-validation:CODE] detail so the router can reconstruct
        // the classification on the other side of the remoting hop.
        // Simulate that by throwing a generic Exception whose message carries
        // the encoded prefix.
        var ct = TestContext.Current.CancellationToken;

        var entry = new DirectoryEntry(
            new Address("agent", RemotedAgentId),
            RemotedAgentId,
            "Remoted Agent",
            "A test agent",
            null,
            DateTimeOffset.UtcNow);
        _factory.DirectoryService
            .ResolveAsync(Arg.Is<Address>(a => a.Scheme == "agent" && a.Id == RemotedAgentId),
                Arg.Any<CancellationToken>())
            .Returns(entry);

        var encodedMessage = new CallerValidationException(
            CallerValidationCodes.MissingThreadId,
            "Domain messages must have a ThreadId").Message;

        var agent = Substitute.For<IAgent>();
        agent.ReceiveAsync(Arg.Any<Message>(), Arg.Any<CancellationToken>())
            .Returns<Task<Message?>>(_ => throw new InvalidOperationException(encodedMessage));
        _factory.AgentProxyResolver
            .Resolve(Arg.Is<string>(s => string.Equals(s, "agent", StringComparison.OrdinalIgnoreCase)),
                RemotedAgentId.ToString("N"))
            .Returns(agent);

        var request = new SendMessageRequest(
            new AddressDto("agent", RemotedAgentId.ToString("N")),
            "HealthCheck",
            null,
            JsonSerializer.SerializeToElement(new { }));

        var response = await _client.PostAsJsonAsync("/api/v1/tenant/messages", request, ct);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ct);
        problem.GetProperty("detail").GetString().ShouldBe("Domain messages must have a ThreadId");
        problem.GetProperty("code").GetString().ShouldBe(CallerValidationCodes.MissingThreadId);
    }

    // #2112: Domain sends with a malformed (non-Guid) ThreadId historically
    // surfaced as a raw 500 from the IThreadRegistry path (an
    // ArgumentException-shaped payload). The endpoint now validates the
    // shape up-front and returns a structured 400 ProblemDetails — the
    // E2E messaging scenarios that pass a stable client-supplied
    // correlation id need a clean signal to distinguish "bad input" from
    // "server failure" rather than a 500.

    [Fact]
    public async Task SendMessage_DomainWithNonGuidThreadId_Returns400()
    {
        var ct = TestContext.Current.CancellationToken;

        // Directory is wired but should never be reached — the threadId
        // shape check fires before routing, so the agent stub is just a
        // safety net for "the validation fired".
        var entry = new DirectoryEntry(
            new Address("agent", NonGuidThreadAgentId),
            NonGuidThreadAgentId,
            "Non-Guid Thread Agent",
            "A test agent",
            null,
            DateTimeOffset.UtcNow);
        _factory.DirectoryService
            .ResolveAsync(Arg.Is<Address>(a => a.Scheme == "agent" && a.Id == NonGuidThreadAgentId),
                Arg.Any<CancellationToken>())
            .Returns(entry);

        var request = new SendMessageRequest(
            new AddressDto("agent", NonGuidThreadAgentId.ToString("N")),
            "Domain",
            "my-human-thread",
            JsonSerializer.SerializeToElement(new { Text = "hello" }));

        var response = await _client.PostAsJsonAsync("/api/v1/tenant/messages", request, ct);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ct);
        problem.GetProperty("status").GetInt32().ShouldBe(400);
        // The detail names the offending field so callers can switch on it
        // without parsing the prose.
        var detail = problem.GetProperty("detail").GetString();
        detail.ShouldNotBeNull();
        detail.ShouldContain("ThreadId");
        detail.ShouldContain("my-human-thread");
    }

    [Fact]
    public async Task SendMessage_DomainWithGuidShapedButUnknownThreadId_Succeeds()
    {
        // #2112: Guid-shaped thread ids that the registry has not seen
        // before are accepted as caller-supplied stable correlation ids —
        // the registry is a participant-set lookup, not a thread-id
        // existence gate. Rejecting unknown Guids here would block legit
        // clients that thread under their own stable id (and would force
        // every send to begin with a registry round-trip). The non-Guid
        // case stays as a 400 (covered above); only well-formed Guids
        // pass through.
        var ct = TestContext.Current.CancellationToken;

        var entry = new DirectoryEntry(
            new Address("agent", UnknownThreadAgentId),
            UnknownThreadAgentId,
            "Unknown Thread Agent",
            "A test agent",
            null,
            DateTimeOffset.UtcNow);
        _factory.DirectoryService
            .ResolveAsync(Arg.Is<Address>(a => a.Scheme == "agent" && a.Id == UnknownThreadAgentId),
                Arg.Any<CancellationToken>())
            .Returns(entry);

        var agent = Substitute.For<IAgent>();
        Message? observed = null;
        agent.ReceiveAsync(Arg.Do<Message>(m => observed = m), Arg.Any<CancellationToken>())
            .Returns((Message?)null);
        _factory.AgentProxyResolver
            .Resolve(Arg.Is<string>(s => string.Equals(s, "agent", StringComparison.OrdinalIgnoreCase)),
                UnknownThreadAgentId.ToString("N"))
            .Returns(agent);

        var freshGuid = Guid.NewGuid().ToString();
        var request = new SendMessageRequest(
            new AddressDto("agent", UnknownThreadAgentId.ToString("N")),
            "Domain",
            freshGuid,
            JsonSerializer.SerializeToElement(new { Text = "hello" }));

        var response = await _client.PostAsJsonAsync("/api/v1/tenant/messages", request, ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<MessageResponse>(cancellationToken: ct);
        body.ShouldNotBeNull();
        body!.ThreadId.ShouldBe(freshGuid);
        observed.ShouldNotBeNull();
        observed!.ThreadId.ShouldBe(freshGuid);
    }

    [Fact]
    public async Task SendMessage_WhenActorThrowsGenericException_Still502()
    {
        // Regression guard: genuine downstream/infra failures must NOT be
        // reclassified as 400. Only CallerValidationException (and its
        // remoting-encoded message form) gets the 400 treatment.
        var ct = TestContext.Current.CancellationToken;

        var entry = new DirectoryEntry(
            new Address("agent", FlakyAgentId),
            FlakyAgentId,
            "Flaky Agent",
            "A test agent",
            null,
            DateTimeOffset.UtcNow);
        _factory.DirectoryService
            .ResolveAsync(Arg.Is<Address>(a => a.Scheme == "agent" && a.Id == FlakyAgentId),
                Arg.Any<CancellationToken>())
            .Returns(entry);

        var agent = Substitute.For<IAgent>();
        agent.ReceiveAsync(Arg.Any<Message>(), Arg.Any<CancellationToken>())
            .Returns<Task<Message?>>(_ => throw new InvalidOperationException("Database unavailable"));
        _factory.AgentProxyResolver
            .Resolve(Arg.Is<string>(s => string.Equals(s, "agent", StringComparison.OrdinalIgnoreCase)),
                FlakyAgentId.ToString("N"))
            .Returns(agent);

        var request = new SendMessageRequest(
            new AddressDto("agent", FlakyAgentId.ToString("N")),
            "HealthCheck",
            null,
            JsonSerializer.SerializeToElement(new { }));

        var response = await _client.PostAsJsonAsync("/api/v1/tenant/messages", request, ct);

        response.StatusCode.ShouldBe(HttpStatusCode.BadGateway);
    }
}
