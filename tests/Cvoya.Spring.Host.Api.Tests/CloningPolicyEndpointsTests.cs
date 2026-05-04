// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Api.Tests;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

using Cvoya.Spring.Core.Cloning;
using Cvoya.Spring.Core.Directory;
using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Host.Api.Models;

using NSubstitute;

using Shouldly;

using Xunit;

/// <summary>
/// HTTP-level tests for <see cref="Endpoints.CloningPolicyEndpoints"/>. Rides
/// the shared <see cref="CustomWebApplicationFactory"/> so the server pipeline
/// mirrors production; the cloning-policy repository sits on the in-memory
/// state store substituted in the factory.
/// </summary>
public class CloningPolicyEndpointsTests : IClassFixture<CustomWebApplicationFactory>
{
    private static readonly Guid Agent_AdaGet_Id = new("00000001-feed-1234-5678-000000000000");
    private static readonly Guid Agent_AdaSet_Id = new("00000002-feed-1234-5678-000000000000");
    private static readonly Guid Agent_MissingAgent_Id = new("00000003-feed-1234-5678-000000000000");
    private static readonly Guid Agent_MissingClear_Id = new("00000004-feed-1234-5678-000000000000");

    private readonly CustomWebApplicationFactory _factory;
    private readonly HttpClient _client;
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    public CloningPolicyEndpointsTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task GetAgentCloningPolicy_AgentNotFound_Returns404()
    {
        var ct = TestContext.Current.CancellationToken;
        _factory.DirectoryService
            .ResolveAsync(new Address("agent", Agent_MissingAgent_Id), Arg.Any<CancellationToken>())
            .Returns((DirectoryEntry?)null);

        var response = await _client.GetAsync(
            $"/api/v1/tenant/agents/{Agent_MissingAgent_Id:N}/cloning-policy", ct);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task GetAgentCloningPolicy_NoPersistedPolicy_ReturnsEmptyShape()
    {
        var ct = TestContext.Current.CancellationToken;
        var address = new Address("agent", Agent_AdaGet_Id);
        _factory.DirectoryService.ResolveAsync(address, Arg.Any<CancellationToken>())
            .Returns(new DirectoryEntry(address, Agent_AdaGet_Id, "Ada", "Ada", null, DateTimeOffset.UtcNow));
        _factory.StateStore
            .GetAsync<AgentCloningPolicy>(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((AgentCloningPolicy?)null);

        var response = await _client.GetAsync(
            $"/api/v1/tenant/agents/{Agent_AdaGet_Id:N}/cloning-policy", ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<AgentCloningPolicyResponse>(_jsonOptions, ct);
        body.ShouldNotBeNull();
        body!.AllowedPolicies.ShouldBeNull();
        body.MaxClones.ShouldBeNull();
        body.MaxDepth.ShouldBeNull();
        body.Budget.ShouldBeNull();
    }

    [Fact]
    public async Task SetAgentCloningPolicy_RoundTripsWireShape()
    {
        var ct = TestContext.Current.CancellationToken;
        var address = new Address("agent", Agent_AdaSet_Id);
        _factory.DirectoryService.ResolveAsync(address, Arg.Any<CancellationToken>())
            .Returns(new DirectoryEntry(address, Agent_AdaSet_Id, "Ada", "Ada", null, DateTimeOffset.UtcNow));

        var request = new AgentCloningPolicyResponse(
            AllowedPolicies: new[] { CloningPolicy.EphemeralNoMemory },
            AllowedAttachmentModes: new[] { AttachmentMode.Detached },
            MaxClones: 4,
            MaxDepth: 2,
            Budget: 7m);

        var putResponse = await _client.PutAsJsonAsync(
            $"/api/v1/tenant/agents/{Agent_AdaSet_Id:N}/cloning-policy", request, _jsonOptions, ct);

        putResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await putResponse.Content.ReadFromJsonAsync<AgentCloningPolicyResponse>(_jsonOptions, ct);
        body.ShouldNotBeNull();
        body!.MaxClones.ShouldBe(4);
        body.MaxDepth.ShouldBe(2);
        body.Budget.ShouldBe(7m);
        body.AllowedPolicies.ShouldBe(new[] { CloningPolicy.EphemeralNoMemory });
    }

    [Fact]
    public async Task DeleteAgentCloningPolicy_AgentNotFound_Returns404()
    {
        var ct = TestContext.Current.CancellationToken;
        _factory.DirectoryService
            .ResolveAsync(new Address("agent", Agent_MissingClear_Id), Arg.Any<CancellationToken>())
            .Returns((DirectoryEntry?)null);

        var response = await _client.DeleteAsync(
            $"/api/v1/tenant/agents/{Agent_MissingClear_Id:N}/cloning-policy", ct);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task TenantCloningPolicy_GetSetClear_RoundTrip()
    {
        var ct = TestContext.Current.CancellationToken;

        // Initial GET returns the empty shape without touching the directory
        // (the tenant surface is not tied to an agent).
        var empty = await _client.GetAsync("/api/v1/tenant/cloning-policy", ct);
        empty.StatusCode.ShouldBe(HttpStatusCode.OK);

        var request = new AgentCloningPolicyResponse(MaxClones: 12);
        var set = await _client.PutAsJsonAsync(
            "/api/v1/tenant/cloning-policy", request, _jsonOptions, ct);
        set.StatusCode.ShouldBe(HttpStatusCode.OK);

        var cleared = await _client.DeleteAsync("/api/v1/tenant/cloning-policy", ct);
        cleared.StatusCode.ShouldBe(HttpStatusCode.NoContent);
    }
}