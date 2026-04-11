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

public class CloneEndpointsTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public CloneEndpointsTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task ListClones_AgentExists_ReturnsEmptyList()
    {
        var ct = TestContext.Current.CancellationToken;
        var agentAddress = new Address("agent", "test-agent");
        var entry = new DirectoryEntry(agentAddress, "actor-1", "Test Agent", "A test agent", "backend", DateTimeOffset.UtcNow);
        _factory.DirectoryService.ResolveAsync(agentAddress, Arg.Any<CancellationToken>()).Returns(entry);

        var response = await _client.GetAsync("/api/v1/agents/test-agent/clones", ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var clones = await response.Content.ReadFromJsonAsync<List<CloneResponse>>(ct);
        clones.Should().BeEmpty();
    }

    [Fact]
    public async Task CreateClone_AgentNotFound_Returns404()
    {
        var ct = TestContext.Current.CancellationToken;
        var agentAddress = new Address("agent", "nonexistent-agent");
        _factory.DirectoryService.ResolveAsync(agentAddress, Arg.Any<CancellationToken>()).Returns((DirectoryEntry?)null);

        var request = new CreateCloneRequest("ephemeral-no-memory", "detached");

        var response = await _client.PostAsJsonAsync("/api/v1/agents/nonexistent-agent/clones", request, ct);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task GetClone_CloneNotFound_Returns404()
    {
        var ct = TestContext.Current.CancellationToken;
        var cloneAddress = new Address("agent", "nonexistent-clone");
        _factory.DirectoryService.ResolveAsync(cloneAddress, Arg.Any<CancellationToken>()).Returns((DirectoryEntry?)null);

        var response = await _client.GetAsync("/api/v1/agents/test-agent/clones/nonexistent-clone", ct);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task DeleteClone_CloneNotFound_Returns404()
    {
        var ct = TestContext.Current.CancellationToken;
        var cloneAddress = new Address("agent", "nonexistent-clone");
        _factory.DirectoryService.ResolveAsync(cloneAddress, Arg.Any<CancellationToken>()).Returns((DirectoryEntry?)null);

        var response = await _client.DeleteAsync("/api/v1/agents/test-agent/clones/nonexistent-clone", ct);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}