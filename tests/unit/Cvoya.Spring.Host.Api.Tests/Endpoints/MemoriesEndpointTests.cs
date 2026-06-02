// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Api.Tests.Endpoints;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

using Cvoya.Spring.Core.Directory;
using Cvoya.Spring.Core.Identifiers;
using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Dapr.Data;
using Cvoya.Spring.Dapr.Data.Entities;
using Cvoya.Spring.Host.Api.Models;

using Microsoft.Extensions.DependencyInjection;

using NSubstitute;

using Shouldly;

using Xunit;

/// <summary>
/// Endpoint coverage for the memory read API (#2342). Exercises the
/// real <c>IMemoryStore</c> path with rows seeded directly into the
/// in-memory EF context. Scope (agent / thread) is derived from each
/// row's <c>thread_id</c> binding (#2997).
/// </summary>
public class MemoriesEndpointTests : IClassFixture<CustomWebApplicationFactory>
{
    private static readonly Guid ActorAda_Id = new("00002711-bbbb-cccc-dddd-000000000000");
    private static readonly Guid ActorEng_Id = new("00002712-bbbb-cccc-dddd-000000000000");

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly CustomWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public MemoriesEndpointTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task GetUnitMemories_KnownUnitNoData_ReturnsEmptyAgentAndThreadLists()
    {
        var ct = TestContext.Current.CancellationToken;
        ArrangeDirectoryHit("unit", "engineering", ActorEng_Id);

        var response = await _client.GetAsync($"/api/v1/tenant/units/{ActorEng_Id:N}/memories", ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var body = await response.Content.ReadFromJsonAsync<MemoriesResponse>(JsonOptions, ct);
        body.ShouldNotBeNull();
        body!.Agent.ShouldBeEmpty();
        body.Thread.ShouldBeEmpty();
    }

    [Fact]
    public async Task GetUnitMemories_UnknownUnit_Returns404()
    {
        var ct = TestContext.Current.CancellationToken;
        ArrangeDirectoryMiss();

        var response = await _client.GetAsync($"/api/v1/tenant/units/{Guid.NewGuid():N}/memories", ct);
        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task GetAgentMemories_KnownAgentNoData_ReturnsEmptyAgentAndThreadLists()
    {
        var ct = TestContext.Current.CancellationToken;
        ArrangeDirectoryHit("agent", "ada", ActorAda_Id);

        var response = await _client.GetAsync($"/api/v1/tenant/agents/{ActorAda_Id:N}/memories", ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var body = await response.Content.ReadFromJsonAsync<MemoriesResponse>(JsonOptions, ct);
        body.ShouldNotBeNull();
        body!.Agent.ShouldBeEmpty();
        body.Thread.ShouldBeEmpty();
    }

    [Fact]
    public async Task GetAgentMemories_UnknownAgent_Returns404()
    {
        var ct = TestContext.Current.CancellationToken;
        ArrangeDirectoryMiss();

        var response = await _client.GetAsync($"/api/v1/tenant/agents/{Guid.NewGuid():N}/memories", ct);
        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task GetAgentMemories_RowsSeeded_ReturnsPartitionedAgentAndThreadLists()
    {
        var ct = TestContext.Current.CancellationToken;
        var agentId = Guid.NewGuid();
        ArrangeDirectoryHit("agent", "rosa", agentId);

        var agentScopedId = SeedMemoryRow(agentId, content: "Remember the design decisions", threadId: null);
        var threadScopedId = SeedMemoryRow(agentId, content: "Working note in this conversation", threadId: Guid.NewGuid());

        var response = await _client.GetAsync($"/api/v1/tenant/agents/{agentId:N}/memories", ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var body = await response.Content.ReadFromJsonAsync<MemoriesResponse>(JsonOptions, ct);
        body.ShouldNotBeNull();
        body!.Agent.Count.ShouldBe(1);
        body.Thread.Count.ShouldBe(1);
        body.Agent[0].Id.ShouldBe(GuidFormatter.Format(agentScopedId));
        body.Agent[0].Scope.ShouldBe("agent");
        body.Thread[0].Id.ShouldBe(GuidFormatter.Format(threadScopedId));
        body.Thread[0].Scope.ShouldBe("thread");
    }

    [Fact]
    public async Task GetAgentMemories_ScopeFilter_RestrictsResponse()
    {
        var ct = TestContext.Current.CancellationToken;
        var agentId = Guid.NewGuid();
        ArrangeDirectoryHit("agent", "rosa-filter", agentId);

        SeedMemoryRow(agentId, content: "Agent-scoped recall", threadId: null);
        SeedMemoryRow(agentId, content: "Thread-scoped note", threadId: Guid.NewGuid());

        var response = await _client.GetAsync(
            $"/api/v1/tenant/agents/{agentId:N}/memories?scope=agent", ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var body = await response.Content.ReadFromJsonAsync<MemoriesResponse>(JsonOptions, ct);
        body.ShouldNotBeNull();
        body!.Agent.Count.ShouldBe(1);
        body.Thread.ShouldBeEmpty();
    }

    [Fact]
    public async Task GetAgentMemories_BadScopeFilter_Returns400()
    {
        var ct = TestContext.Current.CancellationToken;
        ArrangeDirectoryHit("agent", "ada", ActorAda_Id);

        var response = await _client.GetAsync(
            $"/api/v1/tenant/agents/{ActorAda_Id:N}/memories?scope=nonsense", ct);
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task GetAgentMemories_QueryParameter_RestrictsBySearch()
    {
        var ct = TestContext.Current.CancellationToken;
        var agentId = Guid.NewGuid();
        ArrangeDirectoryHit("agent", "rosa-query", agentId);

        SeedMemoryRow(agentId, content: "the quick brown fox", threadId: null);
        SeedMemoryRow(agentId, content: "completely unrelated material", threadId: null);

        var response = await _client.GetAsync(
            $"/api/v1/tenant/agents/{agentId:N}/memories?query=brown", ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var body = await response.Content.ReadFromJsonAsync<MemoriesResponse>(JsonOptions, ct);
        body.ShouldNotBeNull();
        body!.Agent.Count.ShouldBe(1);
        body.Agent[0].Content.GetString().ShouldNotBeNull().ShouldContain("brown");
    }

    [Fact]
    public async Task GetAgentMemoryById_RowSeeded_ReturnsEntry()
    {
        var ct = TestContext.Current.CancellationToken;
        var agentId = Guid.NewGuid();
        ArrangeDirectoryHit("agent", "rosa-by-id", agentId);

        var memoryId = SeedMemoryRow(agentId, content: "specific entry", threadId: null);

        var response = await _client.GetAsync(
            $"/api/v1/tenant/agents/{agentId:N}/memories/{memoryId:N}", ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var body = await response.Content.ReadFromJsonAsync<MemoryEntry>(JsonOptions, ct);
        body.ShouldNotBeNull();
        body!.Id.ShouldBe(GuidFormatter.Format(memoryId));
        body.Content.ValueKind.ShouldBe(JsonValueKind.String);
        body.Content.GetString().ShouldBe("specific entry");
    }

    [Fact]
    public async Task GetAgentMemoryById_StructuredContent_SurfacesNativeJson()
    {
        var ct = TestContext.Current.CancellationToken;
        var agentId = Guid.NewGuid();
        ArrangeDirectoryHit("agent", "rosa-json", agentId);

        var memoryId = SeedMemoryRowRaw(
            agentId,
            rawJsonContent: """{"status":"published","piece":3}""", threadId: null);

        var response = await _client.GetAsync(
            $"/api/v1/tenant/agents/{agentId:N}/memories/{memoryId:N}", ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var body = await response.Content.ReadFromJsonAsync<MemoryEntry>(JsonOptions, ct);
        body.ShouldNotBeNull();
        // content surfaces as a native JSON object, not a stringified blob.
        body!.Content.ValueKind.ShouldBe(JsonValueKind.Object);
        body.Content.GetProperty("status").GetString().ShouldBe("published");
        body.Content.GetProperty("piece").GetInt32().ShouldBe(3);
    }

    [Fact]
    public async Task GetAgentMemoryById_UnknownId_Returns404()
    {
        var ct = TestContext.Current.CancellationToken;
        var agentId = Guid.NewGuid();
        ArrangeDirectoryHit("agent", "rosa-missing", agentId);

        var response = await _client.GetAsync(
            $"/api/v1/tenant/agents/{agentId:N}/memories/{Guid.NewGuid():N}", ct);
        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task GetUnitMemoryById_UnknownUnit_Returns404()
    {
        var ct = TestContext.Current.CancellationToken;
        ArrangeDirectoryMiss();

        var response = await _client.GetAsync(
            $"/api/v1/tenant/units/{Guid.NewGuid():N}/memories/{Guid.NewGuid():N}", ct);
        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    // Seeds a text memory: the jsonb content column holds the text as a
    // JSON string (mirrors how EfMemoryStore serialises a JsonElement).
    // Scope is derived from thread_id (#2997) — no kind argument.
    private Guid SeedMemoryRow(Guid ownerId, string content, Guid? threadId)
        => SeedMemoryRowRaw(ownerId, JsonSerializer.Serialize(content), threadId);

    // Seeds a memory with raw JSON content (a structured object/array, or
    // a pre-serialised JSON string) directly into the jsonb column.
    private Guid SeedMemoryRowRaw(Guid ownerId, string rawJsonContent, Guid? threadId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SpringDbContext>();
        var id = Guid.NewGuid();
        db.Memories.Add(new MemoryEntity
        {
            Id = id,
            OwnerScheme = "agent",
            OwnerId = ownerId,
            // Scope is derived from thread_id (#2997): null => agent-scoped,
            // a value => thread-scoped.
            ThreadId = threadId,
            Content = rawJsonContent,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });
        db.SaveChanges();
        return id;
    }

    private void ArrangeDirectoryHit(string scheme, string displayName, Guid actorId)
    {
        _factory.DirectoryService.ClearReceivedCalls();
        var entry = new DirectoryEntry(
            new Address(scheme, actorId),
            actorId,
            displayName,
            $"{scheme} {displayName}",
            null,
            DateTimeOffset.UtcNow);
        _factory.DirectoryService
            .ResolveAsync(Arg.Is<Address>(a => a.Scheme == scheme && a.Id == actorId),
                Arg.Any<CancellationToken>())
            .Returns(entry);
    }

    private void ArrangeDirectoryMiss()
    {
        _factory.DirectoryService.ClearReceivedCalls();
        _factory.DirectoryService
            .ResolveAsync(Arg.Any<Address>(), Arg.Any<CancellationToken>())
            .Returns((DirectoryEntry?)null);
    }
}
