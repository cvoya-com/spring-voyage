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
/// Endpoint coverage for the memory + topic read API (#2342). The
/// existing test suite shipped against an empty-arrays stub; this
/// rewrite exercises the real <c>IMemoryStore</c> + <c>IMemoryTopicStore</c>
/// path with rows seeded directly into the in-memory EF context.
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
    public async Task GetUnitMemories_KnownUnitNoData_ReturnsEmptyShortAndLongTermLists()
    {
        var ct = TestContext.Current.CancellationToken;
        ArrangeDirectoryHit("unit", "engineering", ActorEng_Id);

        var response = await _client.GetAsync($"/api/v1/tenant/units/{ActorEng_Id:N}/memories", ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var body = await response.Content.ReadFromJsonAsync<MemoriesResponse>(JsonOptions, ct);
        body.ShouldNotBeNull();
        body!.ShortTerm.ShouldBeEmpty();
        body.LongTerm.ShouldBeEmpty();
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
    public async Task GetAgentMemories_KnownAgentNoData_ReturnsEmptyShortAndLongTermLists()
    {
        var ct = TestContext.Current.CancellationToken;
        ArrangeDirectoryHit("agent", "ada", ActorAda_Id);

        var response = await _client.GetAsync($"/api/v1/tenant/agents/{ActorAda_Id:N}/memories", ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var body = await response.Content.ReadFromJsonAsync<MemoriesResponse>(JsonOptions, ct);
        body.ShouldNotBeNull();
        body!.ShortTerm.ShouldBeEmpty();
        body.LongTerm.ShouldBeEmpty();
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
    public async Task GetAgentMemories_RowsSeeded_ReturnsPartitionedShortAndLongTermLists()
    {
        var ct = TestContext.Current.CancellationToken;
        var agentId = Guid.NewGuid();
        ArrangeDirectoryHit("agent", "rosa", agentId);

        var longId = SeedMemoryRow(agentId, kind: 0, content: "Remember the design decisions", threadId: null);
        var shortId = SeedMemoryRow(agentId, kind: 1, content: "Working note in this conversation", threadId: Guid.NewGuid());

        var response = await _client.GetAsync($"/api/v1/tenant/agents/{agentId:N}/memories", ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var body = await response.Content.ReadFromJsonAsync<MemoriesResponse>(JsonOptions, ct);
        body.ShouldNotBeNull();
        body!.ShortTerm.Count.ShouldBe(1);
        body.LongTerm.Count.ShouldBe(1);
        body.ShortTerm[0].Id.ShouldBe(GuidFormatter.Format(shortId));
        body.ShortTerm[0].Kind.ShouldBe("short_term");
        body.LongTerm[0].Id.ShouldBe(GuidFormatter.Format(longId));
        body.LongTerm[0].Kind.ShouldBe("long_term");
    }

    [Fact]
    public async Task GetAgentMemories_KindFilter_RestrictsResponse()
    {
        var ct = TestContext.Current.CancellationToken;
        var agentId = Guid.NewGuid();
        ArrangeDirectoryHit("agent", "rosa-filter", agentId);

        SeedMemoryRow(agentId, kind: 0, content: "Long-term recall", threadId: null);
        SeedMemoryRow(agentId, kind: 1, content: "Short-term note", threadId: Guid.NewGuid());

        var response = await _client.GetAsync(
            $"/api/v1/tenant/agents/{agentId:N}/memories?kind=long_term", ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var body = await response.Content.ReadFromJsonAsync<MemoriesResponse>(JsonOptions, ct);
        body.ShouldNotBeNull();
        body!.LongTerm.Count.ShouldBe(1);
        body.ShortTerm.ShouldBeEmpty();
    }

    [Fact]
    public async Task GetAgentMemories_BadKindFilter_Returns400()
    {
        var ct = TestContext.Current.CancellationToken;
        ArrangeDirectoryHit("agent", "ada", ActorAda_Id);

        var response = await _client.GetAsync(
            $"/api/v1/tenant/agents/{ActorAda_Id:N}/memories?kind=nonsense", ct);
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task GetUnitTopics_KnownUnitNoData_ReturnsEmptyTopics()
    {
        var ct = TestContext.Current.CancellationToken;
        ArrangeDirectoryHit("unit", "engineering", ActorEng_Id);

        var response = await _client.GetAsync($"/api/v1/tenant/units/{ActorEng_Id:N}/topics", ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var body = await response.Content.ReadFromJsonAsync<TopicsResponse>(JsonOptions, ct);
        body.ShouldNotBeNull();
        body!.Topics.ShouldBeEmpty();
    }

    [Fact]
    public async Task GetUnitTopics_RowsSeeded_ReturnsOwnedTopics()
    {
        var ct = TestContext.Current.CancellationToken;
        var unitId = Guid.NewGuid();
        ArrangeDirectoryHit("unit", "ops", unitId);

        var topicId = SeedTopicRow(unitId, "unit", "design-decisions", "where we record the why");

        var response = await _client.GetAsync($"/api/v1/tenant/units/{unitId:N}/topics", ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var body = await response.Content.ReadFromJsonAsync<TopicsResponse>(JsonOptions, ct);
        body.ShouldNotBeNull();
        body!.Topics.Count.ShouldBe(1);
        body.Topics[0].Id.ShouldBe(GuidFormatter.Format(topicId));
        body.Topics[0].Name.ShouldBe("design-decisions");
        body.Topics[0].Description.ShouldBe("where we record the why");
    }

    [Fact]
    public async Task GetAgentTopics_UnknownAgent_Returns404()
    {
        var ct = TestContext.Current.CancellationToken;
        ArrangeDirectoryMiss();

        var response = await _client.GetAsync($"/api/v1/tenant/agents/{Guid.NewGuid():N}/topics", ct);
        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    private Guid SeedMemoryRow(Guid ownerId, int kind, string content, Guid? threadId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SpringDbContext>();
        var id = Guid.NewGuid();
        db.Memories.Add(new MemoryEntity
        {
            Id = id,
            OwnerScheme = "agent",
            OwnerId = ownerId,
            Kind = kind,
            ThreadId = threadId,
            Content = content,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });
        db.SaveChanges();
        return id;
    }

    private Guid SeedTopicRow(Guid ownerId, string scheme, string name, string description)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SpringDbContext>();
        var id = Guid.NewGuid();
        db.MemoryTopics.Add(new MemoryTopicEntity
        {
            Id = id,
            OwnerScheme = scheme,
            OwnerId = ownerId,
            Name = name,
            Description = description,
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
