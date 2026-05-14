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

using NSubstitute;

using Shouldly;

using Xunit;

public class CloneEndpointsTests : IClassFixture<CustomWebApplicationFactory>
{
    private static readonly Guid Agent_CloneA_Id = new("00000001-1234-5678-9abc-000000000000");
    private static readonly Guid Agent_CloneB_Id = new("00000002-1234-5678-9abc-000000000000");
    private static readonly Guid Agent_NonexistentAgent_Id = new("00000003-1234-5678-9abc-000000000000");
    private static readonly Guid Agent_NonexistentClone_Id = new("00000004-1234-5678-9abc-000000000000");
    private static readonly Guid Agent_NonexistentListAgent_Id = new("00000005-1234-5678-9abc-000000000000");
    private static readonly Guid Agent_TestAgent_Id = new("00000006-1234-5678-9abc-000000000000");
    private static readonly Guid Actor1_Id = new("00000007-1234-5678-9abc-000000000000");

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
        var agentId = Agent_TestAgent_Id.ToString("N");
        var agentAddress = new Address("agent", Agent_TestAgent_Id);
        var entry = new DirectoryEntry(agentAddress, Actor1_Id, "Test Agent", "A test agent", "backend", DateTimeOffset.UtcNow);
        _factory.DirectoryService.ResolveAsync(agentAddress, Arg.Any<CancellationToken>()).Returns(entry);
        _factory.StateStore.GetAsync<List<string>>(
            $"{agentId}:{StateKeys.CloneChildren}", Arg.Any<CancellationToken>())
            .Returns((List<string>?)null);

        var response = await _client.GetAsync($"/api/v1/tenant/agents/{agentId}/clones", ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var clones = await response.Content.ReadFromJsonAsync<List<CloneResponse>>(ct);
        clones.ShouldBeEmpty();
    }

    [Fact]
    public async Task ListClones_AgentExists_WithClones_ReturnsCloneList()
    {
        var ct = TestContext.Current.CancellationToken;
        var agentGuid = Guid.NewGuid();
        var agentId = agentGuid.ToString("N");
        var agentAddress = new Address("agent", agentGuid);
        var entry = new DirectoryEntry(agentAddress, agentGuid, "Parent Agent", "A parent", null, DateTimeOffset.UtcNow);
        _factory.DirectoryService.ResolveAsync(agentAddress, Arg.Any<CancellationToken>()).Returns(entry);

        var cloneIds = new List<string> { Agent_CloneA_Id.ToString("N"), Agent_CloneB_Id.ToString("N") };
        _factory.StateStore.GetAsync<List<string>>(
            $"{agentId}:{StateKeys.CloneChildren}", Arg.Any<CancellationToken>())
            .Returns(cloneIds);

        var cloneAHex = Agent_CloneA_Id.ToString("N");
        var cloneBHex = Agent_CloneB_Id.ToString("N");

        var identityA = new CloneIdentity(agentId, cloneAHex, CloningPolicy.EphemeralNoMemory, AttachmentMode.Detached);
        _factory.StateStore.GetAsync<CloneIdentity>(
            $"{cloneAHex}:{StateKeys.CloneIdentity}", Arg.Any<CancellationToken>())
            .Returns(identityA);

        var identityB = new CloneIdentity(agentId, cloneBHex, CloningPolicy.EphemeralWithMemory, AttachmentMode.Attached);
        _factory.StateStore.GetAsync<CloneIdentity>(
            $"{cloneBHex}:{StateKeys.CloneIdentity}", Arg.Any<CancellationToken>())
            .Returns(identityB);

        var cloneEntryA = new DirectoryEntry(new Address("agent", Agent_CloneA_Id), Agent_CloneA_Id, $"Clone:{cloneAHex}", "Clone of list-parent", null, DateTimeOffset.UtcNow);
        _factory.DirectoryService.ResolveAsync(new Address("agent", Agent_CloneA_Id), Arg.Any<CancellationToken>()).Returns(cloneEntryA);

        var cloneEntryB = new DirectoryEntry(new Address("agent", Agent_CloneB_Id), Agent_CloneB_Id, $"Clone:{cloneBHex}", "Clone of list-parent", null, DateTimeOffset.UtcNow);
        _factory.DirectoryService.ResolveAsync(new Address("agent", Agent_CloneB_Id), Arg.Any<CancellationToken>()).Returns(cloneEntryB);

        var response = await _client.GetAsync($"/api/v1/tenant/agents/{agentId}/clones", ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var jsonOptions = new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web)
        {
            Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
        };
        var clones = await response.Content.ReadFromJsonAsync<List<CloneResponse>>(jsonOptions, ct);
        clones!.Count().ShouldBe(2);
        clones![0].CloneId.ShouldBe(Agent_CloneA_Id.ToString("N"));
        clones[0].CloneType.ShouldBe(CloningPolicy.EphemeralNoMemory);
        clones[0].AttachmentMode.ShouldBe(AttachmentMode.Detached);
        clones[0].Status.ShouldBe("active");
        clones[1].CloneId.ShouldBe(Agent_CloneB_Id.ToString("N"));
        clones[1].CloneType.ShouldBe(CloningPolicy.EphemeralWithMemory);
        clones[1].AttachmentMode.ShouldBe(AttachmentMode.Attached);
    }

    [Fact]
    public async Task ListClones_AgentNotFound_Returns404()
    {
        var ct = TestContext.Current.CancellationToken;
        var agentAddress = new Address("agent", Agent_NonexistentListAgent_Id);
        _factory.DirectoryService.ResolveAsync(agentAddress, Arg.Any<CancellationToken>()).Returns((DirectoryEntry?)null);

        var response = await _client.GetAsync(
            $"/api/v1/tenant/agents/{Agent_NonexistentListAgent_Id:N}/clones", ct);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task CreateClone_AgentNotFound_Returns404()
    {
        var ct = TestContext.Current.CancellationToken;
        var agentAddress = new Address("agent", Agent_NonexistentAgent_Id);
        _factory.DirectoryService.ResolveAsync(agentAddress, Arg.Any<CancellationToken>()).Returns((DirectoryEntry?)null);

        var request = new CreateCloneRequest(CloningPolicy.EphemeralNoMemory, AttachmentMode.Detached);

        // Match the server's JsonStringEnumConverter config so the enums
        // serialise as their kebab-case wire names rather than numeric ordinals.
        var jsonOptions = new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web)
        {
            Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
        };
        var response = await _client.PostAsJsonAsync(
            $"/api/v1/tenant/agents/{Agent_NonexistentAgent_Id:N}/clones", request, jsonOptions, ct);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task GetClone_CloneExists_ReturnsCloneWithActualData()
    {
        var ct = TestContext.Current.CancellationToken;
        var cloneGuid = Guid.NewGuid();
        var parentGuid = Guid.NewGuid();
        var cloneId = cloneGuid.ToString("N");
        var parentId = parentGuid.ToString("N");
        var cloneAddress = new Address("agent", cloneGuid);
        var cloneEntry = new DirectoryEntry(cloneAddress, cloneGuid, $"Clone:{cloneId}", $"Clone of {parentId}", null, DateTimeOffset.UtcNow);
        _factory.DirectoryService.ResolveAsync(cloneAddress, Arg.Any<CancellationToken>()).Returns(cloneEntry);

        var identity = new CloneIdentity(parentId, cloneId, CloningPolicy.EphemeralWithMemory, AttachmentMode.Attached);
        _factory.StateStore.GetAsync<CloneIdentity>(
            $"{cloneId}:{StateKeys.CloneIdentity}", Arg.Any<CancellationToken>())
            .Returns(identity);

        var response = await _client.GetAsync($"/api/v1/tenant/agents/{parentId}/clones/{cloneId}", ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        // Response enum fields (CloneType, AttachmentMode) arrive over the
        // wire as their JsonStringEnumMemberName values. Deserialising
        // requires the same converter config the server uses.
        var jsonOptions = new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web)
        {
            Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
        };
        var clone = await response.Content.ReadFromJsonAsync<CloneResponse>(jsonOptions, ct);
        clone.ShouldNotBeNull();
        clone!.CloneId.ShouldBe(cloneId);
        clone.ParentAgentId.ShouldBe(parentId);
        clone.CloneType.ShouldBe(CloningPolicy.EphemeralWithMemory);
        clone.AttachmentMode.ShouldBe(AttachmentMode.Attached);
        clone.Status.ShouldBe("active");
    }

    [Fact]
    public async Task GetClone_CloneNotFound_Returns404()
    {
        var ct = TestContext.Current.CancellationToken;
        var cloneAddress = new Address("agent", Agent_NonexistentClone_Id);
        _factory.DirectoryService.ResolveAsync(cloneAddress, Arg.Any<CancellationToken>()).Returns((DirectoryEntry?)null);

        var response = await _client.GetAsync(
            $"/api/v1/tenant/agents/{Agent_TestAgent_Id:N}/clones/{Agent_NonexistentClone_Id:N}", ct);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task DeleteClone_CloneNotFound_Returns404()
    {
        var ct = TestContext.Current.CancellationToken;
        var cloneAddress = new Address("agent", Agent_NonexistentClone_Id);
        _factory.DirectoryService.ResolveAsync(cloneAddress, Arg.Any<CancellationToken>()).Returns((DirectoryEntry?)null);

        var response = await _client.DeleteAsync(
            $"/api/v1/tenant/agents/{Agent_TestAgent_Id:N}/clones/{Agent_NonexistentClone_Id:N}", ct);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }
}
