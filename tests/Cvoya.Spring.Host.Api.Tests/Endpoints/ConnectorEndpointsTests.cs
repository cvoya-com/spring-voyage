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
///
/// Covers the pivot landed in #714: <c>GET /api/v1/connectors</c> and
/// <c>GET /api/v1/connectors/{slugOrId}</c> now return tenant-installed
/// connectors, <c>DELETE /api/v1/connectors/{slugOrId}</c> uninstalls, and
/// <c>PATCH /api/v1/connectors/{slugOrId}/config</c> replaces the stored
/// config. The retired <c>/installed</c> and <c>/{slug}/install</c> (GET)
/// siblings have no corresponding tests — their semantics now live on the
/// pivoted list/get routes.
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

    // ---- Tenant-scoped list/get (#714) ----

    [Fact]
    public async Task ListConnectors_ReturnsInstalledEnvelopes()
    {
        // The tests share the fixture's in-memory DB via IClassFixture, so
        // prior installs may be present. Prime the stub and assert the
        // envelope contains it — not the exact array length.
        var ct = TestContext.Current.CancellationToken;
        await EnsureStubInstalledAsync(ct);

        var response = await _client.GetAsync("/api/v1/connectors", ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<InstalledConnectorResponse[]>(ct);
        body.ShouldNotBeNull();
        body!.ShouldContain(c => c.TypeSlug == "stub");
        body.ShouldContain(c => c.TypeId == _factory.StubConnectorType.TypeId);
    }

    [Fact]
    public async Task GetConnector_BySlug_WhenInstalled_ReturnsInstalledEnvelope()
    {
        var ct = TestContext.Current.CancellationToken;
        await EnsureStubInstalledAsync(ct);

        var response = await _client.GetAsync("/api/v1/connectors/stub", ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<InstalledConnectorResponse>(ct);
        body.ShouldNotBeNull();
        body!.TypeSlug.ShouldBe("stub");
        body.ConfigUrl.ShouldContain("{unitId}");
        body.ActionsBaseUrl.ShouldBe("/api/v1/connectors/stub/actions");
        body.ConfigSchemaUrl.ShouldBe("/api/v1/connectors/stub/config-schema");
    }

    [Fact]
    public async Task GetConnector_ById_WhenInstalled_ResolvesToSlug()
    {
        var ct = TestContext.Current.CancellationToken;
        await EnsureStubInstalledAsync(ct);

        var byId = await _client.GetAsync(
            $"/api/v1/connectors/{_factory.StubConnectorType.TypeId}", ct);
        byId.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await byId.Content.ReadFromJsonAsync<InstalledConnectorResponse>(ct);
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
    public async Task GetConnector_RegisteredButNotInstalled_Returns404()
    {
        // Pivot contract (#714): a connector type known to the host but not
        // installed on the current tenant MUST surface as 404 from the
        // pivoted get endpoint, not as a registry descriptor.
        var ct = TestContext.Current.CancellationToken;
        await EnsureStubUninstalledAsync(ct);

        var response = await _client.GetAsync("/api/v1/connectors/stub", ct);
        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    // ---- Install lifecycle ----

    [Fact]
    public async Task Install_UnknownSlug_Returns404()
    {
        var ct = TestContext.Current.CancellationToken;
        var response = await _client.PostAsJsonAsync(
            "/api/v1/connectors/not-a-real-connector/install",
            new ConnectorInstallRequest(null),
            ct);
        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Install_StubConnector_SurfacesInListAsInstalledEnvelope()
    {
        var ct = TestContext.Current.CancellationToken;
        var install = await _client.PostAsJsonAsync(
            "/api/v1/connectors/stub/install",
            new ConnectorInstallRequest(null),
            ct);
        install.StatusCode.ShouldBe(HttpStatusCode.OK);

        var listResponse = await _client.GetAsync("/api/v1/connectors", ct);
        var list = await listResponse.Content.ReadFromJsonAsync<InstalledConnectorResponse[]>(ct);
        list.ShouldNotBeNull();
        list.ShouldContain(c => c.TypeSlug == "stub");
        list.ShouldContain(c => c.TypeId == _factory.StubConnectorType.TypeId);
    }

    [Fact]
    public async Task Install_ByTypeId_ResolvesToSlug()
    {
        var ct = TestContext.Current.CancellationToken;
        var install = await _client.PostAsJsonAsync(
            $"/api/v1/connectors/{_factory.StubConnectorType.TypeId}/install",
            new ConnectorInstallRequest(null),
            ct);
        install.StatusCode.ShouldBe(HttpStatusCode.OK);

        var getResponse = await _client.GetAsync("/api/v1/connectors/stub", ct);
        getResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await getResponse.Content.ReadFromJsonAsync<InstalledConnectorResponse>(ct);
        body!.TypeSlug.ShouldBe("stub");
    }

    [Fact]
    public async Task Uninstall_RemovesFromListAndFlipsGetTo404()
    {
        var ct = TestContext.Current.CancellationToken;
        await EnsureStubInstalledAsync(ct);

        // DELETE /{slugOrId} (was DELETE /{slug}/install pre-#714).
        var uninstall = await _client.DeleteAsync("/api/v1/connectors/stub", ct);
        uninstall.StatusCode.ShouldBe(HttpStatusCode.NoContent);

        var getResponse = await _client.GetAsync("/api/v1/connectors/stub", ct);
        getResponse.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Uninstall_UnknownSlug_Returns404()
    {
        var ct = TestContext.Current.CancellationToken;
        var response = await _client.DeleteAsync("/api/v1/connectors/not-a-real-connector", ct);
        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    // ---- Unit connector pointer surface ----

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

    // ---- Helpers ----

    private async Task EnsureStubInstalledAsync(CancellationToken ct)
    {
        var response = await _client.PostAsJsonAsync(
            "/api/v1/connectors/stub/install",
            new ConnectorInstallRequest(null),
            ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    private async Task EnsureStubUninstalledAsync(CancellationToken ct)
    {
        // Idempotent — the server accepts DELETE on an uninstalled connector
        // (install service soft-deletes the row, no-op when absent).
        await _client.DeleteAsync("/api/v1/connectors/stub", ct);
    }
}