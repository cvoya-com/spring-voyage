// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Api.Tests.Contract;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using Cvoya.Spring.Core.Directory;
using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Dapr.Actors;
using Cvoya.Spring.Host.Api.Models;

using NSubstitute;

using Shouldly;

using Xunit;

/// <summary>
/// Semantic contract tests for the <c>/api/v1/messages</c> surface (#1248 / C1.3).
/// </summary>
public class MessageContractTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public MessageContractTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task SendMessage_HappyPath_MatchesContract()
    {
        var ct = TestContext.Current.CancellationToken;

        var entry = new DirectoryEntry(
            new Address("agent", "contract-send-target"),
            "actor-contract-send",
            "Contract Send Target",
            "An agent for contract tests",
            null,
            DateTimeOffset.UtcNow);
        _factory.DirectoryService
            .ResolveAsync(
                Arg.Is<Address>(a => a.Scheme == "agent" && a.Path == "contract-send-target"),
                Arg.Any<CancellationToken>())
            .Returns(entry);

        // Return a non-null reply so MessageResponse.responsePayload is a
        // JSON object on the wire. The committed openapi.json declares
        // `responsePayload` as `oneOf: [null, JsonElement]` where JsonElement
        // is the empty schema `{}` (matches anything). With a null payload
        // both branches would match and oneOf would reject — see follow-up
        // for the spec cleanup. A non-null reply matches only the JsonElement
        // branch and validates cleanly.
        var reply = new Message(
            Guid.NewGuid(),
            new Address("agent", "contract-send-target"),
            new Address("human", "local-dev-user"),
            MessageType.Domain,
            "contract-conv-1",
            JsonSerializer.SerializeToElement(new { ack = "received" }),
            DateTimeOffset.UtcNow);
        var agent = Substitute.For<IAgent>();
        agent.ReceiveAsync(Arg.Any<Message>(), Arg.Any<CancellationToken>())
            .Returns(reply);
        _factory.AgentProxyResolver
            .Resolve(Arg.Is<string>(s => string.Equals(s, "agent", StringComparison.OrdinalIgnoreCase)),
                "actor-contract-send")
            .Returns(agent);

        var request = new SendMessageRequest(
            new AddressDto("agent", "contract-send-target"),
            "Domain",
            "contract-conv-1",
            JsonSerializer.SerializeToElement(new { Text = "hello from contract test" }));

        var response = await _client.PostAsJsonAsync("/api/v1/messages", request, ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var body = await response.Content.ReadAsStringAsync(ct);
        OpenApiContract.AssertResponse("/api/v1/messages", "post", "200", body);
    }

    [Fact]
    public async Task SendMessage_InvalidType_MatchesProblemDetailsContract()
    {
        var ct = TestContext.Current.CancellationToken;

        var request = new SendMessageRequest(
            new AddressDto("agent", "contract-bad-type"),
            "TotallyMadeUpType",
            null,
            JsonSerializer.SerializeToElement(new { }));

        var response = await _client.PostAsJsonAsync("/api/v1/messages", request, ct);
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        var body = await response.Content.ReadAsStringAsync(ct);
        OpenApiContract.AssertResponse(
            "/api/v1/messages", "post", "400", body, "application/problem+json");
    }
}