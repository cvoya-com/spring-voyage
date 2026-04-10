// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Api.Tests;

using System.Net;
using System.Net.Http.Json;
using Cvoya.Spring.Core.Directory;
using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Host.Api.Models;
using FluentAssertions;
using NSubstitute;
using Xunit;

public class AgentEndpointsTests : IClassFixture<CustomWebApplicationFactory>
{
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

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var agents = await response.Content.ReadFromJsonAsync<List<AgentResponse>>(ct);
        agents.Should().HaveCount(1);
        agents![0].Name.Should().Be("test-agent");
        agents[0].DisplayName.Should().Be("Test Agent");
        agents[0].Role.Should().Be("backend");
    }

    [Fact]
    public async Task CreateAgent_RegistersAndReturnsCreated()
    {
        var ct = TestContext.Current.CancellationToken;
        var request = new CreateAgentRequest("new-agent", "New Agent", "A brand new agent", "frontend");

        var response = await _client.PostAsJsonAsync("/api/v1/agents", request, ct);

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        response.Headers.Location!.ToString().Should().Contain("/api/v1/agents/new-agent");

        await _factory.DirectoryService.Received(1).RegisterAsync(
            Arg.Is<DirectoryEntry>(e =>
                e.Address.Scheme == "agent" &&
                e.Address.Path == "new-agent" &&
                e.DisplayName == "New Agent"),
            Arg.Any<CancellationToken>());
    }
}
