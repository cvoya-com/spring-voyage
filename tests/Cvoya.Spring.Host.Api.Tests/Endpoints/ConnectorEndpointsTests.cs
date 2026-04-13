// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Api.Tests.Endpoints;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using Cvoya.Spring.Connectors;
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