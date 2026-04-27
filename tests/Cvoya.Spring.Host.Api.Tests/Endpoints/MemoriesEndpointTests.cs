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
        ArrangeDirectoryHit("unit", "engineering", "actor-eng");

        var response = await _client.GetAsync("/api/v1/tenant/units/engineering/memories", ct);
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

        var response = await _client.GetAsync("/api/v1/tenant/units/ghost/memories", ct);
        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task GetAgentMemories_KnownAgent_ReturnsEmptyShortAndLongTermLists()
    {
        var ct = TestContext.Current.CancellationToken;
        ArrangeDirectoryHit("agent", "ada", "actor-ada");

        var response = await _client.GetAsync("/api/v1/tenant/agents/ada/memories", ct);
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

        var response = await _client.GetAsync("/api/v1/tenant/agents/ghost/memories", ct);
        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    private void ArrangeDirectoryHit(string scheme, string path, string actorId)
    {
        _factory.DirectoryService.ClearReceivedCalls();
        var entry = new DirectoryEntry(
            new Address(scheme, path),
            actorId,
            path,
            $"{scheme} {path}",
            null,
            DateTimeOffset.UtcNow);
        _factory.DirectoryService
            .ResolveAsync(Arg.Is<Address>(a => a.Scheme == scheme && a.Path == path),
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