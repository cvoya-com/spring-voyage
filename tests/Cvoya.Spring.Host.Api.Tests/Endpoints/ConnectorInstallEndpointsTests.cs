// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Api.Tests.Endpoints;

using System.Net;
using System.Net.Http.Json;

using Cvoya.Spring.Host.Api.Models;

using Shouldly;

using Xunit;

/// <summary>
/// Integration tests for the per-tenant connector install surface under
/// <c>/api/v1/connectors/*/install</c>. The test factory registers a
/// single stub <c>IConnectorType</c> named "stub"; these tests install,
/// list, uninstall, and patch it via HTTP.
/// </summary>
public class ConnectorInstallEndpointsTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public ConnectorInstallEndpointsTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task ListInstalled_Returns200WithParseableArray()
    {
        // Smoke: every test in this file shares the factory's in-memory
        // DB via IClassFixture, so prior installs may be present here.
        // Assert the envelope, not the contents.
        var ct = TestContext.Current.CancellationToken;
        var response = await _client.GetAsync("/api/v1/connectors/installed", ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<InstalledConnectorResponse[]>(ct);
        body.ShouldNotBeNull();
    }

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
    public async Task Install_StubConnector_SurfacesInInstalledList()
    {
        var ct = TestContext.Current.CancellationToken;
        var install = await _client.PostAsJsonAsync(
            "/api/v1/connectors/stub/install",
            new ConnectorInstallRequest(null),
            ct);
        install.StatusCode.ShouldBe(HttpStatusCode.OK);

        var listResponse = await _client.GetAsync("/api/v1/connectors/installed", ct);
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

        var getResponse = await _client.GetAsync("/api/v1/connectors/stub/install", ct);
        getResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await getResponse.Content.ReadFromJsonAsync<InstalledConnectorResponse>(ct);
        body!.TypeSlug.ShouldBe("stub");
    }

    [Fact]
    public async Task GetInstall_Uninstalled_Returns404()
    {
        var ct = TestContext.Current.CancellationToken;
        // Ensure not installed — this test is order-independent, so
        // explicitly uninstall first.
        await _client.DeleteAsync("/api/v1/connectors/stub/install", ct);
        var response = await _client.GetAsync("/api/v1/connectors/stub/install", ct);
        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Uninstall_RemovesFromInstalledList()
    {
        var ct = TestContext.Current.CancellationToken;
        await _client.PostAsJsonAsync(
            "/api/v1/connectors/stub/install",
            new ConnectorInstallRequest(null),
            ct);

        var uninstall = await _client.DeleteAsync("/api/v1/connectors/stub/install", ct);
        uninstall.StatusCode.ShouldBe(HttpStatusCode.NoContent);

        var getResponse = await _client.GetAsync("/api/v1/connectors/stub/install", ct);
        getResponse.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }
}