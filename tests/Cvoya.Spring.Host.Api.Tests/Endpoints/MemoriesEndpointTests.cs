// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Api.Tests.Endpoints;

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

/// <summary>
/// Integration tests for the stub memory-inspector endpoints shipped in
/// SVR-memories (umbrella #815, plan §4 / §13). Both routes return
/// populated-empty payloads in v2.0 — the real backing store ships in
/// <c>V21-memory-write</c>. These tests assert the contract shape + 404
/// semantics so downstream Memory-tab wiring can rely on them.
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
    public async Task GetUnitMemories_KnownUnit_ReturnsEmptyShortAndLongTermLists()
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
    public async Task GetAgentMemories_KnownAgent_ReturnsEmptyShortAndLongTermLists()
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
