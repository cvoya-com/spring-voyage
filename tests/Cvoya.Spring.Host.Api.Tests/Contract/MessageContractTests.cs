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
    private static readonly Guid ActorContractNullPayload_Id = new("00002711-bbbb-cccc-dddd-000000000000");
    private static readonly Guid ActorContractSend_Id = new("00002712-bbbb-cccc-dddd-000000000000");

    private static readonly Guid Agent_ContractNullPayloadTarget_Id = new("00000001-feed-1234-5678-000000000000");
    private static readonly Guid Agent_ContractSendTarget_Id = new("00000002-feed-1234-5678-000000000000");
    private static readonly Guid Human_LocalDevUser_Id = new("00000003-feed-1234-5678-000000000000");

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
            new Address("agent", Agent_ContractSendTarget_Id),
            ActorContractSend_Id,
            "Contract Send Target",
            "An agent for contract tests",
            null,
            DateTimeOffset.UtcNow);
        _factory.DirectoryService
            .ResolveAsync(
                Arg.Is<Address>(a => a.Scheme == "agent" && a.Id == Agent_ContractSendTarget_Id),
                Arg.Any<CancellationToken>())
            .Returns(entry);

        // #1254 fixed the openapi.json shape so MessageResponse.responsePayload
        // is now a bare `$ref` to JsonElement (the empty schema), which matches
        // null and any other JSON value. Earlier revisions of this test had to
        // force a non-null reply because the broken `oneOf:[null, JsonElement]`
        // wrapper rejected null instances; the workaround is no longer needed.
        // Keeping the structured reply anyway exercises the more interesting
        // wire shape (a JSON object body) and matches what real receivers
        // emit.
        var reply = new Message(
            Guid.NewGuid(),
            new Address("agent", Agent_ContractSendTarget_Id),
            new Address("human", Human_LocalDevUser_Id),
            MessageType.Domain,
            "contract-conv-1",
            JsonSerializer.SerializeToElement(new { ack = "received" }),
            DateTimeOffset.UtcNow);
        var agent = Substitute.For<IAgent>();
        agent.ReceiveAsync(Arg.Any<Message>(), Arg.Any<CancellationToken>())
            .Returns(reply);
        _factory.AgentProxyResolver
            .Resolve(Arg.Is<string>(s => string.Equals(s, "agent", StringComparison.OrdinalIgnoreCase)),
                ActorContractSend_Id.ToString("N"))
            .Returns(agent);

        var request = new SendMessageRequest(
            new AddressDto("agent", Agent_ContractSendTarget_Id.ToString("N")),
            "Domain",
            "contract-conv-1",
            JsonSerializer.SerializeToElement(new { Text = "hello from contract test" }));

        var response = await _client.PostAsJsonAsync("/api/v1/tenant/messages", request, ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var body = await response.Content.ReadAsStringAsync(ct);
        OpenApiContract.AssertResponse("/api/v1/tenant/messages", "post", "200", body);
    }

    [Fact]
    public async Task SendMessage_InvalidType_MatchesProblemDetailsContract()
    {
        var ct = TestContext.Current.CancellationToken;

        var request = new SendMessageRequest(
            new AddressDto("agent", Guid.NewGuid().ToString("N")),
            "TotallyMadeUpType",
            null,
            JsonSerializer.SerializeToElement(new { }));

        var response = await _client.PostAsJsonAsync("/api/v1/tenant/messages", request, ct);
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        var body = await response.Content.ReadAsStringAsync(ct);
        OpenApiContract.AssertResponse(
            "/api/v1/tenant/messages", "post", "400", body, "application/problem+json");
    }

    [Fact]
    public async Task SendMessage_NullResponsePayload_MatchesContract()
    {
        // Regression guard for #1254. Before the openapi.json cleanup,
        // MessageResponse.responsePayload was declared as
        // `oneOf:[null, $ref to JsonElement]` and the JsonElement schema
        // was `{}`. A null instance matched both branches, so strict
        // JSON Schema 2020-12 evaluators rejected this perfectly valid
        // wire shape. Now the property is a bare `$ref` to JsonElement
        // and null validates cleanly.
        var ct = TestContext.Current.CancellationToken;

        var entry = new DirectoryEntry(
            new Address("agent", Agent_ContractNullPayloadTarget_Id),
            ActorContractNullPayload_Id,
            "Contract Null Payload Target",
            "An agent that returns no reply payload",
            null,
            DateTimeOffset.UtcNow);
        _factory.DirectoryService
            .ResolveAsync(
                Arg.Is<Address>(a => a.Scheme == "agent" && a.Id == Agent_ContractNullPayloadTarget_Id),
                Arg.Any<CancellationToken>())
            .Returns(entry);

        // Returning `null` from ReceiveAsync threads a null payload all
        // the way through to MessageResponse.responsePayload. Pre-fix
        // this body would fail validation on the responsePayload slot
        // even though the runtime accepted it.
        var agent = Substitute.For<IAgent>();
        agent.ReceiveAsync(Arg.Any<Message>(), Arg.Any<CancellationToken>())
            .Returns((Message?)null);
        _factory.AgentProxyResolver
            .Resolve(Arg.Is<string>(s => string.Equals(s, "agent", StringComparison.OrdinalIgnoreCase)),
                ActorContractNullPayload_Id.ToString("N"))
            .Returns(agent);

        var request = new SendMessageRequest(
            new AddressDto("agent", Agent_ContractNullPayloadTarget_Id.ToString("N")),
            "Domain",
            "contract-conv-null",
            JsonSerializer.SerializeToElement(new { Text = "hello" }));

        var response = await _client.PostAsJsonAsync("/api/v1/tenant/messages", request, ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var body = await response.Content.ReadAsStringAsync(ct);
        OpenApiContract.AssertResponse("/api/v1/tenant/messages", "post", "200", body);
    }
}