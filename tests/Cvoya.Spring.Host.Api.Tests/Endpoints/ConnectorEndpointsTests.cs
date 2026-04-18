// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Api.Tests.Endpoints;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using Cvoya.Spring.Connectors;
using Cvoya.Spring.Core.Directory;
using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Host.Api.Models;

using NSubstitute;

using Shouldly;

using Xunit;

/// <summary>
/// Integration tests for the generic, connector-agnostic surface under
/// <c>/api/v1/connectors</c> and <c>/api/v1/units/{id}/connector</c>. The
/// host registers whatever <see cref="IConnectorType"/> services are in DI
/// — the test factory injects a stub so these tests stay independent of any
/// concrete connector package.
/// </summary>
public class ConnectorEndpointsTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public ConnectorEndpointsTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task ListConnectors_ReturnsEveryRegisteredType()
    {
        var ct = TestContext.Current.CancellationToken;
        var response = await _client.GetAsync("/api/v1/connectors", ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<ConnectorTypeResponse[]>(ct);
        body.ShouldNotBeNull();
        body!.ShouldContain(c => c.TypeSlug == "stub");
    }

    [Fact]
    public async Task GetConnector_BySlug_ReturnsEnvelope()
    {
        var ct = TestContext.Current.CancellationToken;
        var response = await _client.GetAsync("/api/v1/connectors/stub", ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<ConnectorTypeResponse>(ct);
        body.ShouldNotBeNull();
        body!.TypeSlug.ShouldBe("stub");
        body.ConfigUrl.ShouldContain("{unitId}");
    }

    [Fact]
    public async Task GetConnector_ById_ReturnsSameEnvelopeAsBySlug()
    {
        var ct = TestContext.Current.CancellationToken;
        var byId = await _client.GetAsync(
            $"/api/v1/connectors/{_factory.StubConnectorType.TypeId}", ct);
        byId.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await byId.Content.ReadFromJsonAsync<ConnectorTypeResponse>(ct);
        body.ShouldNotBeNull();
        body!.TypeSlug.ShouldBe("stub");
    }

    [Fact]
    public async Task GetConnector_UnknownSlug_Returns404()
    {
        var ct = TestContext.Current.CancellationToken;
        var response = await _client.GetAsync("/api/v1/connectors/nope", ct);
        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task GetUnitConnector_Unbound_Returns404()
    {
        var ct = TestContext.Current.CancellationToken;
        _factory.ConnectorConfigStore.ClearReceivedCalls();
        _factory.ConnectorConfigStore.GetAsync("some-unit", Arg.Any<CancellationToken>())
            .Returns((UnitConnectorBinding?)null);

        var response = await _client.GetAsync("/api/v1/units/some-unit/connector", ct);
        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task GetUnitConnector_Bound_ReturnsPointer()
    {
        var ct = TestContext.Current.CancellationToken;
        _factory.ConnectorConfigStore.ClearReceivedCalls();
        var binding = new UnitConnectorBinding(
            _factory.StubConnectorType.TypeId,
            JsonSerializer.SerializeToElement(new { anything = true }));
        _factory.ConnectorConfigStore.GetAsync("u1", Arg.Any<CancellationToken>())
            .Returns(binding);

        var response = await _client.GetAsync("/api/v1/units/u1/connector", ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<UnitConnectorPointerResponse>(ct);
        body.ShouldNotBeNull();
        body!.TypeSlug.ShouldBe("stub");
        body.ConfigUrl.ShouldBe("/api/v1/connectors/stub/units/u1/config");
    }

    [Fact]
    public async Task ListConnectorBindings_ReturnsEveryUnitBoundToRequestedType()
    {
        // Happy path (#520): two units are bound to the stub connector type,
        // one is bound to a different (orphan) type, one is unbound. The bulk
        // endpoint must collapse the server-side walk into a single array
        // containing only the two matching rows — the portal's N+1 fan-out
        // (introduced in #516) is deleted in favour of this call.
        var ct = TestContext.Current.CancellationToken;
        _factory.DirectoryService.ClearReceivedCalls();
        _factory.ConnectorConfigStore.ClearReceivedCalls();

        var entries = new List<DirectoryEntry>
        {
            new(new Address("unit", "alpha"), "actor-alpha", "Alpha", "", null, DateTimeOffset.UtcNow),
            new(new Address("unit", "beta"), "actor-beta", "Beta", "", null, DateTimeOffset.UtcNow),
            new(new Address("unit", "gamma"), "actor-gamma", "Gamma", "", null, DateTimeOffset.UtcNow),
            new(new Address("unit", "delta"), "actor-delta", "Delta", "", null, DateTimeOffset.UtcNow),
            new(new Address("agent", "noise"), "actor-noise", "Noise", "", null, DateTimeOffset.UtcNow),
        };
        _factory.DirectoryService.ListAllAsync(Arg.Any<CancellationToken>()).Returns(entries);

        var stubBinding = new UnitConnectorBinding(
            _factory.StubConnectorType.TypeId,
            JsonSerializer.SerializeToElement(new { anything = true }));
        var otherBinding = new UnitConnectorBinding(
            Guid.Parse("11111111-1111-1111-1111-111111111111"),
            JsonSerializer.SerializeToElement(new { }));

        _factory.ConnectorConfigStore.GetAsync("alpha", Arg.Any<CancellationToken>()).Returns(stubBinding);
        _factory.ConnectorConfigStore.GetAsync("beta", Arg.Any<CancellationToken>()).Returns((UnitConnectorBinding?)null);
        _factory.ConnectorConfigStore.GetAsync("gamma", Arg.Any<CancellationToken>()).Returns(otherBinding);
        _factory.ConnectorConfigStore.GetAsync("delta", Arg.Any<CancellationToken>()).Returns(stubBinding);

        var response = await _client.GetAsync("/api/v1/connectors/stub/bindings", ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var body = await response.Content.ReadFromJsonAsync<ConnectorUnitBindingResponse[]>(ct);
        body.ShouldNotBeNull();
        body!.Length.ShouldBe(2);
        body.Select(r => r.UnitId).OrderBy(x => x).ShouldBe(new[] { "alpha", "delta" });
        body.ShouldAllBe(r => r.TypeSlug == "stub");
        body.ShouldAllBe(r => r.TypeId == _factory.StubConnectorType.TypeId);
        body.Single(r => r.UnitId == "alpha").UnitDisplayName.ShouldBe("Alpha");
        body.Single(r => r.UnitId == "alpha").ConfigUrl.ShouldBe("/api/v1/connectors/stub/units/alpha/config");
        body.Single(r => r.UnitId == "alpha").ActionsBaseUrl.ShouldBe("/api/v1/connectors/stub/actions");
    }

    [Fact]
    public async Task ListConnectorBindings_EmptyWhenNothingBound()
    {
        // Empty path: three units exist but none bind the requested type, so
        // the endpoint yields [] rather than 404. Matches the portal's "Bound
        // units (0)" empty state used by /connectors/{slug}.
        var ct = TestContext.Current.CancellationToken;
        _factory.DirectoryService.ClearReceivedCalls();
        _factory.ConnectorConfigStore.ClearReceivedCalls();

        var entries = new List<DirectoryEntry>
        {
            new(new Address("unit", "u1"), "actor-u1", "Unit 1", "", null, DateTimeOffset.UtcNow),
            new(new Address("unit", "u2"), "actor-u2", "Unit 2", "", null, DateTimeOffset.UtcNow),
        };
        _factory.DirectoryService.ListAllAsync(Arg.Any<CancellationToken>()).Returns(entries);
        _factory.ConnectorConfigStore.GetAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((UnitConnectorBinding?)null);

        var response = await _client.GetAsync("/api/v1/connectors/stub/bindings", ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var body = await response.Content.ReadFromJsonAsync<ConnectorUnitBindingResponse[]>(ct);
        body.ShouldNotBeNull();
        body!.ShouldBeEmpty();
    }

    [Fact]
    public async Task ListConnectorBindings_UnknownConnector_Returns404()
    {
        var ct = TestContext.Current.CancellationToken;
        var response = await _client.GetAsync("/api/v1/connectors/does-not-exist/bindings", ct);
        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task ListConnectorBindings_OnlyWalksUnitsVisibleInDirectory()
    {
        // Boundary-filtered path (#497): the directory surface the endpoint
        // walks is the same one UnitEndpoints.ListUnitsAsync consumes, so any
        // visibility filter the cloud extension wraps around it applies to
        // bindings too. Here we simulate that filter by returning only a
        // subset of the tenant's units from ListAllAsync — the bulk endpoint
        // must not leak a binding for a unit that was filtered out, even
        // though the config store still carries its row.
        var ct = TestContext.Current.CancellationToken;
        _factory.DirectoryService.ClearReceivedCalls();
        _factory.ConnectorConfigStore.ClearReceivedCalls();

        var visibleEntries = new List<DirectoryEntry>
        {
            new(new Address("unit", "visible"), "actor-visible", "Visible", "", null, DateTimeOffset.UtcNow),
        };
        _factory.DirectoryService.ListAllAsync(Arg.Any<CancellationToken>()).Returns(visibleEntries);

        var binding = new UnitConnectorBinding(
            _factory.StubConnectorType.TypeId,
            JsonSerializer.SerializeToElement(new { }));
        _factory.ConnectorConfigStore.GetAsync("visible", Arg.Any<CancellationToken>()).Returns(binding);
        // Hidden unit has a matching binding in the store, but ListAllAsync
        // never surfaces it. The endpoint must not call the store for it.
        _factory.ConnectorConfigStore.GetAsync("hidden", Arg.Any<CancellationToken>()).Returns(binding);

        var response = await _client.GetAsync("/api/v1/connectors/stub/bindings", ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var body = await response.Content.ReadFromJsonAsync<ConnectorUnitBindingResponse[]>(ct);
        body.ShouldNotBeNull();
        body!.Length.ShouldBe(1);
        body[0].UnitId.ShouldBe("visible");

        await _factory.ConnectorConfigStore.DidNotReceive()
            .GetAsync("hidden", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DeleteUnitConnector_ClearsBindingAndRuntime()
    {
        var ct = TestContext.Current.CancellationToken;
        _factory.ConnectorConfigStore.ClearReceivedCalls();
        _factory.ConnectorRuntimeStore.ClearReceivedCalls();

        var response = await _client.DeleteAsync("/api/v1/units/u2/connector", ct);

        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        await _factory.ConnectorConfigStore.Received(1).ClearAsync("u2", Arg.Any<CancellationToken>());
        await _factory.ConnectorRuntimeStore.Received(1).ClearAsync("u2", Arg.Any<CancellationToken>());
    }
}