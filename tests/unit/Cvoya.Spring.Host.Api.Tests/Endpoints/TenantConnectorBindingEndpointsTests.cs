// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Api.Tests.Endpoints;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using Cvoya.Spring.Connectors;
using Cvoya.Spring.Host.Api.Models;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

using NSubstitute;

using Shouldly;

using Xunit;

/// <summary>
/// Integration coverage for the tenant-scoped connector binding endpoints
/// introduced by ADR-0061 §1. These tests use a dedicated factory derived
/// from <see cref="CustomWebApplicationFactory"/> that registers a
/// tenant-scoped stub connector (the default fixture's stub is per-unit).
/// </summary>
public class TenantConnectorBindingEndpointsTests : IClassFixture<TenantConnectorBindingEndpointsTests.Factory>
{
    private const string TenantSlug = "tenant-stub";
    private const string UnitSlug = "stub";

    private readonly Factory _factory;
    private readonly HttpClient _client;

    public TenantConnectorBindingEndpointsTests(Factory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task GetBinding_NotBound_Returns404()
    {
        var ct = TestContext.Current.CancellationToken;
        await EnsureUnbound(ct);

        var response = await _client.GetAsync($"/api/v1/tenant/connectors/{TenantSlug}/binding", ct);
        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task PutBinding_RoundTrips()
    {
        var ct = TestContext.Current.CancellationToken;
        await EnsureUnbound(ct);

        var config = JsonSerializer.SerializeToElement(new
        {
            team_id = "T123",
            bot_user_id = "U999",
        });
        var put = await _client.PutAsJsonAsync(
            $"/api/v1/tenant/connectors/{TenantSlug}/binding",
            new TenantConnectorBindingRequest(config),
            ct);
        put.StatusCode.ShouldBe(HttpStatusCode.OK);

        var body = await put.Content.ReadFromJsonAsync<TenantConnectorBindingResponse>(ct);
        body.ShouldNotBeNull();
        body!.ConnectorSlug.ShouldBe(TenantSlug);
        body.TypeId.ShouldBe(_factory.TenantStubTypeId);
        body.Config.GetProperty("team_id").GetString().ShouldBe("T123");

        var get = await _client.GetAsync($"/api/v1/tenant/connectors/{TenantSlug}/binding", ct);
        get.StatusCode.ShouldBe(HttpStatusCode.OK);
        var fetched = await get.Content.ReadFromJsonAsync<TenantConnectorBindingResponse>(ct);
        fetched!.Config.GetProperty("bot_user_id").GetString().ShouldBe("U999");
    }

    [Fact]
    public async Task DeleteBinding_RemovesRow()
    {
        var ct = TestContext.Current.CancellationToken;
        var config = JsonSerializer.SerializeToElement(new { team_id = "T999" });
        await _client.PutAsJsonAsync(
            $"/api/v1/tenant/connectors/{TenantSlug}/binding",
            new TenantConnectorBindingRequest(config),
            ct);

        var delete = await _client.DeleteAsync($"/api/v1/tenant/connectors/{TenantSlug}/binding", ct);
        delete.StatusCode.ShouldBe(HttpStatusCode.NoContent);

        var get = await _client.GetAsync($"/api/v1/tenant/connectors/{TenantSlug}/binding", ct);
        get.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task DeleteBinding_NotBound_Returns404()
    {
        var ct = TestContext.Current.CancellationToken;
        await EnsureUnbound(ct);

        var response = await _client.DeleteAsync($"/api/v1/tenant/connectors/{TenantSlug}/binding", ct);
        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task GetBinding_UnknownSlug_Returns404()
    {
        var ct = TestContext.Current.CancellationToken;
        var response = await _client.GetAsync("/api/v1/tenant/connectors/no-such-slug/binding", ct);
        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task GetBinding_AgainstUnitScopedConnector_Returns400()
    {
        // ADR-0061 §1: the binding endpoints are tenant-scoped only;
        // hitting them against a per-unit connector must return 400 so
        // the caller redirects to the per-unit config surface.
        var ct = TestContext.Current.CancellationToken;
        var response = await _client.GetAsync($"/api/v1/tenant/connectors/{UnitSlug}/binding", ct);
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task PutBinding_AgainstUnitScopedConnector_Returns400()
    {
        var ct = TestContext.Current.CancellationToken;
        var config = JsonSerializer.SerializeToElement(new { });
        var response = await _client.PutAsJsonAsync(
            $"/api/v1/tenant/connectors/{UnitSlug}/binding",
            new TenantConnectorBindingRequest(config),
            ct);
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    private async Task EnsureUnbound(CancellationToken ct)
    {
        // DELETE is idempotent only on bound state; absent rows return 404.
        // Suppress the response so the next test step starts on a clean slate.
        _ = await _client.DeleteAsync($"/api/v1/tenant/connectors/{TenantSlug}/binding", ct);
    }

    /// <summary>
    /// Custom factory that adds a tenant-scoped stub connector on top of
    /// the inherited per-unit stub. Both stubs are registered as
    /// <see cref="IConnectorType"/> services so the connector-endpoint
    /// resolver finds both via slug lookup.
    /// </summary>
    public class Factory : CustomWebApplicationFactory
    {
        public Guid TenantStubTypeId { get; } = new("00000000-0000-0000-0000-00000000feed");

        public IConnectorType TenantStubConnectorType { get; }

        public Factory()
        {
            TenantStubConnectorType = CreateTenantStub(TenantStubTypeId);
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureServices(services =>
            {
                services.AddSingleton(TenantStubConnectorType);
            });
        }

        private static IConnectorType CreateTenantStub(Guid typeId)
        {
            var stub = Substitute.For<IConnectorType>();
            stub.TypeId.Returns(typeId);
            stub.Slug.Returns(TenantSlug);
            stub.DisplayName.Returns("Tenant Stub");
            stub.Description.Returns("Test-only tenant-scoped connector stub.");
            stub.ConfigType.Returns(typeof(object));
            stub.BindingScope.Returns(BindingScope.Tenant);
            stub.GetConfigSchemaAsync(Arg.Any<CancellationToken>())
                .Returns(Task.FromResult<JsonElement?>(null));
            stub.OnUnitStartingAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(Task.CompletedTask);
            stub.OnUnitStoppingAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(Task.CompletedTask);
            return stub;
        }
    }
}
