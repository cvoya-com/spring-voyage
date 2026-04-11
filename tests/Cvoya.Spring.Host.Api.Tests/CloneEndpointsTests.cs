// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Api.Tests;

using System.Net;
using System.Net.Http.Json;

using Cvoya.Spring.Core.Cloning;
using Cvoya.Spring.Core.Directory;
using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Dapr.Actors;
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
    public async Task ListClones_AgentExists_NoClones_ReturnsEmptyList()
    {
        var ct = TestContext.Current.CancellationToken;
        var agentAddress = new Address("agent", "test-agent");
        var entry = new DirectoryEntry(agentAddress, "actor-1", "Test Agent", "A test agent", "backend", DateTimeOffset.UtcNow);
        _factory.DirectoryService.ResolveAsync(agentAddress, Arg.Any<CancellationToken>()).Returns(entry);
        _factory.StateStore.GetAsync<List<string>>(
            $"test-agent:{StateKeys.CloneChildren}", Arg.Any<CancellationToken>())
            .Returns((List<string>?)null);

        var response = await _client.GetAsync("/api/v1/agents/test-agent/clones", ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var clones = await response.Content.ReadFromJsonAsync<List<CloneResponse>>(ct);
        clones.Should().BeEmpty();
    }

    [Fact]
    public async Task ListClones_AgentExists_WithClones_ReturnsCloneList()
    {
        var ct = TestContext.Current.CancellationToken;
        var agentId = "list-parent";
        var agentAddress = new Address("agent", agentId);
        var entry = new DirectoryEntry(agentAddress, agentId, "Parent Agent", "A parent", null, DateTimeOffset.UtcNow);
        _factory.DirectoryService.ResolveAsync(agentAddress, Arg.Any<CancellationToken>()).Returns(entry);

        var cloneIds = new List<string> { "clone-a", "clone-b" };
        _factory.StateStore.GetAsync<List<string>>(
            $"{agentId}:{StateKeys.CloneChildren}", Arg.Any<CancellationToken>())
            .Returns(cloneIds);

        var identityA = new CloneIdentity(agentId, "clone-a", CloningPolicy.EphemeralNoMemory, AttachmentMode.Detached);
        _factory.StateStore.GetAsync<CloneIdentity>(
            $"clone-a:{StateKeys.CloneIdentity}", Arg.Any<CancellationToken>())
            .Returns(identityA);

        var identityB = new CloneIdentity(agentId, "clone-b", CloningPolicy.EphemeralWithMemory, AttachmentMode.Attached);
        _factory.StateStore.GetAsync<CloneIdentity>(
            $"clone-b:{StateKeys.CloneIdentity}", Arg.Any<CancellationToken>())
            .Returns(identityB);

        var cloneEntryA = new DirectoryEntry(new Address("agent", "clone-a"), "clone-a", "Clone:clone-a", "Clone of list-parent", null, DateTimeOffset.UtcNow);
        _factory.DirectoryService.ResolveAsync(new Address("agent", "clone-a"), Arg.Any<CancellationToken>()).Returns(cloneEntryA);

        var cloneEntryB = new DirectoryEntry(new Address("agent", "clone-b"), "clone-b", "Clone:clone-b", "Clone of list-parent", null, DateTimeOffset.UtcNow);
        _factory.DirectoryService.ResolveAsync(new Address("agent", "clone-b"), Arg.Any<CancellationToken>()).Returns(cloneEntryB);

        var response = await _client.GetAsync($"/api/v1/agents/{agentId}/clones", ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var clones = await response.Content.ReadFromJsonAsync<List<CloneResponse>>(ct);
        clones.Should().HaveCount(2);
        clones![0].CloneId.Should().Be("clone-a");
        clones[0].CloneType.Should().Be("ephemeral-no-memory");
        clones[0].AttachmentMode.Should().Be("detached");
        clones[0].Status.Should().Be("active");
        clones[1].CloneId.Should().Be("clone-b");
        clones[1].CloneType.Should().Be("ephemeral-with-memory");
        clones[1].AttachmentMode.Should().Be("attached");
    }

    [Fact]
    public async Task ListClones_AgentNotFound_Returns404()
    {
        var ct = TestContext.Current.CancellationToken;
        var agentAddress = new Address("agent", "nonexistent-list-agent");
        _factory.DirectoryService.ResolveAsync(agentAddress, Arg.Any<CancellationToken>()).Returns((DirectoryEntry?)null);

        var response = await _client.GetAsync("/api/v1/agents/nonexistent-list-agent/clones", ct);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
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
    public async Task GetClone_CloneExists_ReturnsCloneWithActualData()
    {
        var ct = TestContext.Current.CancellationToken;
        var cloneId = "real-clone";
        var parentId = "real-parent";
        var cloneAddress = new Address("agent", cloneId);
        var cloneEntry = new DirectoryEntry(cloneAddress, cloneId, $"Clone:{cloneId}", $"Clone of {parentId}", null, DateTimeOffset.UtcNow);
        _factory.DirectoryService.ResolveAsync(cloneAddress, Arg.Any<CancellationToken>()).Returns(cloneEntry);

        var identity = new CloneIdentity(parentId, cloneId, CloningPolicy.EphemeralWithMemory, AttachmentMode.Attached);
        _factory.StateStore.GetAsync<CloneIdentity>(
            $"{cloneId}:{StateKeys.CloneIdentity}", Arg.Any<CancellationToken>())
            .Returns(identity);

        var response = await _client.GetAsync($"/api/v1/agents/{parentId}/clones/{cloneId}", ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var clone = await response.Content.ReadFromJsonAsync<CloneResponse>(ct);
        clone.Should().NotBeNull();
        clone!.CloneId.Should().Be(cloneId);
        clone.ParentAgentId.Should().Be(parentId);
        clone.CloneType.Should().Be("ephemeral-with-memory");
        clone.AttachmentMode.Should().Be("attached");
        clone.Status.Should().Be("active");
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