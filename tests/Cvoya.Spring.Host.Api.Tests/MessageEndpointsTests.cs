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
}