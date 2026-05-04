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

using global::Dapr.Actors;
using global::Dapr.Actors.Client;

using NSubstitute;

using Shouldly;

using Xunit;

/// <summary>
/// Integration tests for unit metadata flows: <c>POST /api/v1/units</c>
/// with Model/Color, <c>PATCH /api/v1/units/{id}</c>, and the GET projection
/// exposing Model/Color.
/// </summary>
public class UnitMetadataEndpointTests : IClassFixture<CustomWebApplicationFactory>
{
    private static readonly Guid ActorEngineering_Id = new("00000001-1234-5678-9abc-000000000000");

    private const string UnitDisplayName = "engineering";
    private static readonly Guid ActorId_Guid = ActorEngineering_Id;
    private static readonly string ActorId = ActorId_Guid.ToString("N");
    // Post-#1629 URL paths carry the unit's Guid hex.
    private static readonly string UnitName = ActorId;

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
                new Address("unit", ActorId_Guid),
                ActorId_Guid,
                "Engineering",
                "Engineering unit",
                null,
                DateTimeOffset.UtcNow));

        var createResponse = await _client.PostAsJsonAsync(
            "/api/v1/tenant/units",
            new CreateUnitRequest(UnitName, "Engineering", "Engineering unit", "claude-opus-4", "#336699", IsTopLevel: true),
            ct);

        createResponse.StatusCode.ShouldBe(HttpStatusCode.Created);

        await proxy.Received(1).SetMetadataAsync(
            Arg.Is<UnitMetadata>(m => m.Model == "claude-opus-4" && m.Color == "#336699"),
            Arg.Any<CancellationToken>());

        // GET should surface the new fields from actor metadata.
        var getResponse = await _client.GetAsync($"/api/v1/tenant/units/{UnitName}", ct);
        getResponse.StatusCode.ShouldBe(HttpStatusCode.OK);

        var body = await getResponse.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(body);
        // GET returns either a UnitResponse directly or { unit, details } wrapper.
        var unitElement = doc.RootElement.TryGetProperty("unit", out var wrapped)
            ? wrapped
            : doc.RootElement;
        unitElement.GetProperty("model").GetString().ShouldBe("claude-opus-4");
        unitElement.GetProperty("color").GetString().ShouldBe("#336699");
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
            $"/api/v1/tenant/units/{UnitName}",
            new UpdateUnitRequest(null, null, "gpt-4o-mini", "#ff00aa"),
            ct);

        patchResponse.StatusCode.ShouldBe(HttpStatusCode.OK);

        await proxy.Received(1).SetMetadataAsync(
            Arg.Is<UnitMetadata>(m => m.Model == "gpt-4o-mini" && m.Color == "#ff00aa"),
            Arg.Any<CancellationToken>());

        var body = await patchResponse.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(body);
        doc.RootElement.GetProperty("model").GetString().ShouldBe("gpt-4o-mini");
        doc.RootElement.GetProperty("color").GetString().ShouldBe("#ff00aa");
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
            $"/api/v1/tenant/units/{UnitName}",
            new UpdateUnitRequest(Model: "gpt-4o"),
            ct);

        patchResponse.StatusCode.ShouldBe(HttpStatusCode.OK);

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
    public async Task PatchUnit_UpdatesDisplayNameAndDescription_RoutesThroughDirectory()
    {
        var ct = TestContext.Current.CancellationToken;
        var proxy = Substitute.For<IUnitActor>();
        proxy.GetStatusAsync(Arg.Any<CancellationToken>()).Returns(UnitStatus.Draft);
        proxy.GetMetadataAsync(Arg.Any<CancellationToken>())
            .Returns(new UnitMetadata(null, null, null, null));

        ArrangeResolved(proxy);

        _factory.DirectoryService
            .UpdateEntryAsync(
                Arg.Any<Address>(),
                Arg.Any<string?>(),
                Arg.Any<string?>(),
                Arg.Any<CancellationToken>())
            .Returns(ci => new DirectoryEntry(
                new Address("unit", ActorId_Guid),
                ActorId_Guid,
                ci.ArgAt<string?>(1) ?? "Engineering",
                ci.ArgAt<string?>(2) ?? "Engineering unit",
                null,
                DateTimeOffset.UtcNow));

        var patchResponse = await _client.PatchAsJsonAsync(
            $"/api/v1/tenant/units/{UnitName}",
            new UpdateUnitRequest(DisplayName: "Eng Team", Description: "Builds stuff"),
            ct);

        patchResponse.StatusCode.ShouldBe(HttpStatusCode.OK);

        // DisplayName/Description must be forwarded to the directory, not persisted on the actor.
        await _factory.DirectoryService.Received(1).UpdateEntryAsync(
            Arg.Is<Address>(a => a.Scheme == "unit" && a.Path == UnitName),
            "Eng Team",
            "Builds stuff",
            Arg.Any<CancellationToken>());

        // Actor is still invoked so the audit-trail StateChanged event is emitted (#123).
        await proxy.Received(1).SetMetadataAsync(
            Arg.Is<UnitMetadata>(m =>
                m.DisplayName == "Eng Team" &&
                m.Description == "Builds stuff"),
            Arg.Any<CancellationToken>());

        var body = await patchResponse.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(body);
        doc.RootElement.GetProperty("displayName").GetString().ShouldBe("Eng Team");
        doc.RootElement.GetProperty("description").GetString().ShouldBe("Builds stuff");
    }

    [Fact]
    public async Task PatchUnit_OnlyModelChange_DoesNotCallDirectoryUpdate()
    {
        var ct = TestContext.Current.CancellationToken;
        var proxy = Substitute.For<IUnitActor>();
        proxy.GetStatusAsync(Arg.Any<CancellationToken>()).Returns(UnitStatus.Draft);
        proxy.GetMetadataAsync(Arg.Any<CancellationToken>())
            .Returns(new UnitMetadata(null, null, "gpt-4o", null));

        ArrangeResolved(proxy);

        var response = await _client.PatchAsJsonAsync(
            $"/api/v1/tenant/units/{UnitName}",
            new UpdateUnitRequest(Model: "gpt-4o"),
            ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        await _factory.DirectoryService.DidNotReceive().UpdateEntryAsync(
            Arg.Any<Address>(),
            Arg.Any<string?>(),
            Arg.Any<string?>(),
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
            $"/api/v1/tenant/units/{Guid.NewGuid():N}",
            new UpdateUnitRequest(Model: "gpt-4o"),
            ct);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    private void ArrangeResolved(IUnitActor proxy)
    {
        ResetFactoryMocks();

        var entry = new DirectoryEntry(
            new Address("unit", ActorId_Guid),
            ActorId_Guid,
            "Engineering",
            "Engineering unit",
            null,
            DateTimeOffset.UtcNow);

        _factory.DirectoryService
            .ResolveAsync(Arg.Is<Address>(a => a.Scheme == "unit" && a.Id == ActorId_Guid), Arg.Any<CancellationToken>())
            .Returns(entry);

        _factory.ActorProxyFactory
            .CreateActorProxy<IUnitActor>(Arg.Is<global::Dapr.Actors.ActorId>(a => a.GetId() == ActorId), Arg.Any<string>())
            .Returns(proxy);
    }

    private void ResetFactoryMocks()
    {
        _factory.DirectoryService.ClearReceivedCalls();
        _factory.ActorProxyFactory.ClearReceivedCalls();
        _factory.UnitContainerLifecycle.ClearReceivedCalls();
    }
}