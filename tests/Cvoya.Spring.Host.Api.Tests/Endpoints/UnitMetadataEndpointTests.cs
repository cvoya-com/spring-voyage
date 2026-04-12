// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Api.Tests.Endpoints;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using Cvoya.Spring.Core.Directory;
using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Core.Units;
using Cvoya.Spring.Dapr.Actors;
using Cvoya.Spring.Host.Api.Models;

using FluentAssertions;

using global::Dapr.Actors;
using global::Dapr.Actors.Client;

using NSubstitute;

using Xunit;

/// <summary>
/// Integration tests for unit metadata flows: <c>POST /api/v1/units</c>
/// with Model/Color, <c>PATCH /api/v1/units/{id}</c>, and the GET projection
/// exposing Model/Color.
/// </summary>
public class UnitMetadataEndpointTests : IClassFixture<CustomWebApplicationFactory>
{
    private const string UnitName = "engineering";
    private const string ActorId = "actor-engineering";

    private readonly CustomWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public UnitMetadataEndpointTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task CreateUnit_WithModelAndColor_PersistsViaActor_AndGetReturnsThem()
    {
        var ct = TestContext.Current.CancellationToken;
        var proxy = Substitute.For<IUnitActor>();
        proxy.GetStatusAsync(Arg.Any<CancellationToken>()).Returns(UnitStatus.Draft);
        proxy.GetMetadataAsync(Arg.Any<CancellationToken>())
            .Returns(new UnitMetadata(null, null, "claude-opus-4", "#336699"));

        ResetFactoryMocks();
        _factory.ActorProxyFactory
            .CreateActorProxy<IUnitActor>(Arg.Any<ActorId>(), Arg.Any<string>())
            .Returns(proxy);

        _factory.DirectoryService.RegisterAsync(Arg.Any<DirectoryEntry>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        // Allow subsequent GET to resolve the entry we just registered.
        _factory.DirectoryService
            .ResolveAsync(Arg.Is<Address>(a => a.Scheme == "unit" && a.Path == UnitName), Arg.Any<CancellationToken>())
            .Returns(ci => new DirectoryEntry(
                new Address("unit", UnitName),
                ActorId,
                "Engineering",
                "Engineering unit",
                null,
                DateTimeOffset.UtcNow));

        var createResponse = await _client.PostAsJsonAsync(
            "/api/v1/units",
            new CreateUnitRequest(UnitName, "Engineering", "Engineering unit", "claude-opus-4", "#336699"),
            ct);

        createResponse.StatusCode.Should().Be(HttpStatusCode.Created);

        await proxy.Received(1).SetMetadataAsync(
            Arg.Is<UnitMetadata>(m => m.Model == "claude-opus-4" && m.Color == "#336699"),
            Arg.Any<CancellationToken>());

        // GET should surface the new fields from actor metadata.
        var getResponse = await _client.GetAsync($"/api/v1/units/{UnitName}", ct);
        getResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await getResponse.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(body);
        // GET returns either a UnitResponse directly or { unit, details } wrapper.
        var unitElement = doc.RootElement.TryGetProperty("unit", out var wrapped)
            ? wrapped
            : doc.RootElement;
        unitElement.GetProperty("model").GetString().Should().Be("claude-opus-4");
        unitElement.GetProperty("color").GetString().Should().Be("#336699");
    }

    [Fact]
    public async Task PatchUnit_UpdatesModelAndColor_SubsequentGetReturnsNewValues()
    {
        var ct = TestContext.Current.CancellationToken;
        var proxy = Substitute.For<IUnitActor>();
        proxy.GetStatusAsync(Arg.Any<CancellationToken>()).Returns(UnitStatus.Draft);
        proxy.GetMetadataAsync(Arg.Any<CancellationToken>())
            .Returns(new UnitMetadata(null, null, "gpt-4o-mini", "#ff00aa"));

        ArrangeResolved(proxy);

        var patchResponse = await _client.PatchAsJsonAsync(
            $"/api/v1/units/{UnitName}",
            new UpdateUnitRequest(null, null, "gpt-4o-mini", "#ff00aa"),
            ct);

        patchResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        await proxy.Received(1).SetMetadataAsync(
            Arg.Is<UnitMetadata>(m => m.Model == "gpt-4o-mini" && m.Color == "#ff00aa"),
            Arg.Any<CancellationToken>());

        var body = await patchResponse.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(body);
        doc.RootElement.GetProperty("model").GetString().Should().Be("gpt-4o-mini");
        doc.RootElement.GetProperty("color").GetString().Should().Be("#ff00aa");
    }

    [Fact]
    public async Task PatchUnit_PartialBody_OnlyForwardsProvidedFields()
    {
        var ct = TestContext.Current.CancellationToken;
        var proxy = Substitute.For<IUnitActor>();
        proxy.GetStatusAsync(Arg.Any<CancellationToken>()).Returns(UnitStatus.Draft);
        proxy.GetMetadataAsync(Arg.Any<CancellationToken>())
            .Returns(new UnitMetadata(null, null, "gpt-4o", null));

        ArrangeResolved(proxy);

        var patchResponse = await _client.PatchAsJsonAsync(
            $"/api/v1/units/{UnitName}",
            new UpdateUnitRequest(Model: "gpt-4o"),
            ct);

        patchResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        // The forwarded metadata record must carry Model only; other fields stay null.
        await proxy.Received(1).SetMetadataAsync(
            Arg.Is<UnitMetadata>(m =>
                m.Model == "gpt-4o" &&
                m.Color == null &&
                m.DisplayName == null &&
                m.Description == null),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PatchUnit_UnknownUnit_Returns404()
    {
        var ct = TestContext.Current.CancellationToken;
        ResetFactoryMocks();

        _factory.DirectoryService
            .ResolveAsync(Arg.Any<Address>(), Arg.Any<CancellationToken>())
            .Returns((DirectoryEntry?)null);

        var response = await _client.PatchAsJsonAsync(
            "/api/v1/units/does-not-exist",
            new UpdateUnitRequest(Model: "gpt-4o"),
            ct);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    private void ArrangeResolved(IUnitActor proxy)
    {
        ResetFactoryMocks();

        var entry = new DirectoryEntry(
            new Address("unit", UnitName),
            ActorId,
            "Engineering",
            "Engineering unit",
            null,
            DateTimeOffset.UtcNow);

        _factory.DirectoryService
            .ResolveAsync(Arg.Is<Address>(a => a.Scheme == "unit" && a.Path == UnitName), Arg.Any<CancellationToken>())
            .Returns(entry);

        _factory.ActorProxyFactory
            .CreateActorProxy<IUnitActor>(Arg.Is<ActorId>(a => a.GetId() == ActorId), Arg.Any<string>())
            .Returns(proxy);
    }

    private void ResetFactoryMocks()
    {
        _factory.DirectoryService.ClearReceivedCalls();
        _factory.ActorProxyFactory.ClearReceivedCalls();
        _factory.UnitContainerLifecycle.ClearReceivedCalls();
    }
}