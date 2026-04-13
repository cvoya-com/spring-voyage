// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Api.Tests;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

using Cvoya.Spring.Core.Directory;
using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Host.Api.Models;

using NSubstitute;

using Shouldly;

using Xunit;

public class AgentEndpointsTests : IClassFixture<CustomWebApplicationFactory>
{
    // Server serialises enums as strings (Program.cs#134); tests must match.
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly CustomWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public AgentEndpointsTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task ListAgents_ReturnsAgentsFromDirectory()
    {
        var ct = TestContext.Current.CancellationToken;
        var entries = new List<DirectoryEntry>
        {
            new(new Address("agent", "test-agent"), "actor-1", "Test Agent", "A test agent", "backend", DateTimeOffset.UtcNow),
            new(new Address("unit", "test-unit"), "actor-2", "Test Unit", "A test unit", null, DateTimeOffset.UtcNow)
        };
        _factory.DirectoryService.ListAllAsync(Arg.Any<CancellationToken>()).Returns(entries);

        var response = await _client.GetAsync("/api/v1/agents", ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var agents = await response.Content.ReadFromJsonAsync<List<AgentResponse>>(JsonOptions, ct);
        agents!.Count().ShouldBe(1);
        agents![0].Name.ShouldBe("test-agent");
        agents[0].DisplayName.ShouldBe("Test Agent");
        agents[0].Role.ShouldBe("backend");
    }

    [Fact]
    public async Task CreateAgent_RegistersAndReturnsCreated()
    {
        var ct = TestContext.Current.CancellationToken;
        var request = new CreateAgentRequest("new-agent", "New Agent", "A brand new agent", "frontend");

        var response = await _client.PostAsJsonAsync("/api/v1/agents", request, ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        response.Headers.Location!.ToString().ShouldContain("/api/v1/agents/new-agent");

        await _factory.DirectoryService.Received(1).RegisterAsync(
            Arg.Is<DirectoryEntry>(e =>
                e.Address.Scheme == "agent" &&
                e.Address.Path == "new-agent" &&
                e.DisplayName == "New Agent"),
            Arg.Any<CancellationToken>());
    }
}