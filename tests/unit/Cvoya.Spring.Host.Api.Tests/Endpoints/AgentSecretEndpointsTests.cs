// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Api.Tests.Endpoints;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

using Cvoya.Spring.Core.Directory;
using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Core.Secrets;
using Cvoya.Spring.Core.Tenancy;
using Cvoya.Spring.Dapr.Data;
using Cvoya.Spring.Host.Api.Models;

using Microsoft.Extensions.DependencyInjection;

using NSubstitute;

using Shouldly;

using Xunit;

/// <summary>
/// HTTP-level tests for the agent-scoped secret endpoint group (#1741).
/// Mirrors <see cref="SecretEndpointsTests"/> in shape — verifies the
/// existence check, basic CRUD round-trips, the propagate-flag's
/// "ignored at agent scope" semantics, and that the agent-scope rows
/// land in the registry as <see cref="SecretScope.Agent"/>.
/// </summary>
public class AgentSecretEndpointsTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;
    private readonly HttpClient _client;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    public AgentSecretEndpointsTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task Get_Returns404_WhenAgentMissing()
    {
        var ct = TestContext.Current.CancellationToken;
        var missingId = Guid.NewGuid();
        _factory.DirectoryService.ResolveAsync(
            new Address("agent", missingId), Arg.Any<CancellationToken>())
            .Returns((DirectoryEntry?)null);

        var response = await _client.GetAsync($"/api/v1/tenant/agents/{missingId:N}/secrets", ct);
        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Post_PassThrough_Stores_AsAgentScopedRow()
    {
        var ct = TestContext.Current.CancellationToken;
        var agent = NewAgent();
        StubAgent(agent);

        var response = await _client.PostAsJsonAsync(
            $"/api/v1/tenant/agents/{agent}/secrets",
            new CreateSecretRequest("anthropic-api-key", "sk-ant-abc"),
            ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Created);

        var body = await response.Content.ReadFromJsonAsync<CreateSecretResponse>(JsonOptions, ct);
        body.ShouldNotBeNull();
        body!.Name.ShouldBe("anthropic-api-key");
        body.Scope.ShouldBe(SecretScope.Agent);

        // Plaintext must never appear in any response body.
        var raw = await response.Content.ReadAsStringAsync(ct);
        raw.ShouldNotContain("sk-ant-abc");

        // Registry row exists for the agent under SecretScope.Agent.
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SpringDbContext>();
        var tenant = scope.ServiceProvider.GetRequiredService<ITenantContext>().CurrentTenantId;
        var row = db.SecretRegistryEntries.SingleOrDefault(
            e => e.TenantId == tenant
                && e.Scope == SecretScope.Agent
                && e.OwnerId == agent.Id
                && e.Name == "anthropic-api-key");
        row.ShouldNotBeNull();
        // Origin recorded correctly.
        row!.Origin.ShouldBe(SecretOrigin.PlatformOwned);
    }

    [Fact]
    public async Task Post_Returns_LocationHeader_WithAgentUrl()
    {
        var ct = TestContext.Current.CancellationToken;
        var agent = NewAgent();
        StubAgent(agent);

        var response = await _client.PostAsJsonAsync(
            $"/api/v1/tenant/agents/{agent}/secrets",
            new CreateSecretRequest("kv-ref", ExternalStoreKey: "kv://vault/x"),
            ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        response.Headers.Location!.ToString().ShouldContain($"/api/v1/tenant/agents/{agent}/secrets/kv-ref");
    }

    [Fact]
    public async Task List_RoundtripsAgentSecret()
    {
        var ct = TestContext.Current.CancellationToken;
        var agent = NewAgent();
        StubAgent(agent);

        await _client.PostAsJsonAsync(
            $"/api/v1/tenant/agents/{agent}/secrets",
            new CreateSecretRequest("sk", ExternalStoreKey: "kv://x"), ct);

        var response = await _client.GetAsync($"/api/v1/tenant/agents/{agent}/secrets", ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var body = await response.Content.ReadFromJsonAsync<SecretsListResponse>(JsonOptions, ct);
        body.ShouldNotBeNull();
        body!.Secrets.ShouldContain(s => s.Name == "sk" && s.Scope == SecretScope.Agent);
    }

    [Fact]
    public async Task Delete_RemovesAgentScopedRegistryRow()
    {
        var ct = TestContext.Current.CancellationToken;
        var agent = NewAgent();
        StubAgent(agent);

        await _client.PostAsJsonAsync(
            $"/api/v1/tenant/agents/{agent}/secrets",
            new CreateSecretRequest("temp", ExternalStoreKey: "kv://vault/x"), ct);

        var deleteResponse = await _client.DeleteAsync(
            $"/api/v1/tenant/agents/{agent}/secrets/temp", ct);
        deleteResponse.StatusCode.ShouldBe(HttpStatusCode.NoContent);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SpringDbContext>();
        db.SecretRegistryEntries
            .Where(e => e.Scope == SecretScope.Agent && e.OwnerId == agent.Id && e.Name == "temp")
            .ShouldBeEmpty();
    }

    [Fact]
    public async Task Rotate_ExistingAgentSecret_AppendsNewVersion()
    {
        var ct = TestContext.Current.CancellationToken;
        var agent = NewAgent();
        StubAgent(agent);

        await _client.PostAsJsonAsync(
            $"/api/v1/tenant/agents/{agent}/secrets",
            new CreateSecretRequest("token", "v1"), ct);

        var response = await _client.PutAsJsonAsync(
            $"/api/v1/tenant/agents/{agent}/secrets/token",
            new RotateSecretRequest(Value: "v2"),
            ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var body = await response.Content.ReadFromJsonAsync<RotateSecretResponse>(JsonOptions, ct);
        body.ShouldNotBeNull();
        body!.Version.ShouldBe(2);
        body.Scope.ShouldBe(SecretScope.Agent);
    }

    [Fact]
    public async Task Post_PropagateFlagAtAgentScope_StoresAsTrue()
    {
        // Agent secrets have no descendants — the resolver chain ends
        // at agent. The endpoint accepts a propagate field for shape
        // symmetry but coerces it to true so the registry stays
        // consistent. This test pins that contract: even when the
        // operator sends propagate=false, the persisted row reads
        // true (the only structurally meaningful value).
        var ct = TestContext.Current.CancellationToken;
        var agent = NewAgent();
        StubAgent(agent);

        var response = await _client.PostAsJsonAsync(
            $"/api/v1/tenant/agents/{agent}/secrets",
            new CreateSecretRequest("k", "v", Propagate: false),
            ct);
        response.StatusCode.ShouldBe(HttpStatusCode.Created);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SpringDbContext>();
        var row = db.SecretRegistryEntries.Single(
            e => e.Scope == SecretScope.Agent && e.OwnerId == agent.Id && e.Name == "k");
        row.Propagate.ShouldBeTrue();
    }

    [Fact]
    public async Task Post_PropagateFalse_AtUnitScope_PersistsFalse()
    {
        // Counterpart contract: propagate=false IS honoured at unit
        // scope (#1737). The Agent / Tenant / Platform paths coerce
        // to true, but the unit endpoint must persist the operator's
        // chosen value verbatim so the resolver's parent-unit walk
        // (#1737) sees the correct flag.
        var ct = TestContext.Current.CancellationToken;
        var unit = Guid.NewGuid();
        var unitAddress = new Address("unit", unit);
        _factory.DirectoryService.ResolveAsync(unitAddress, Arg.Any<CancellationToken>())
            .Returns(new DirectoryEntry(unitAddress, unit, unit.ToString("N"), "test", null, DateTimeOffset.UtcNow));

        var response = await _client.PostAsJsonAsync(
            $"/api/v1/tenant/units/{unit:N}/secrets",
            new CreateSecretRequest("k", "v", Propagate: false),
            ct);
        response.StatusCode.ShouldBe(HttpStatusCode.Created);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SpringDbContext>();
        var row = db.SecretRegistryEntries.Single(
            e => e.Scope == SecretScope.Unit && e.OwnerId == unit && e.Name == "k");
        row.Propagate.ShouldBeFalse();
    }

    [Fact]
    public async Task Versions_ListsRetainedRows_ForAgentSecret()
    {
        var ct = TestContext.Current.CancellationToken;
        var agent = NewAgent();
        StubAgent(agent);

        await _client.PostAsJsonAsync(
            $"/api/v1/tenant/agents/{agent}/secrets",
            new CreateSecretRequest("token", "v1"), ct);
        await _client.PutAsJsonAsync(
            $"/api/v1/tenant/agents/{agent}/secrets/token",
            new RotateSecretRequest(Value: "v2"), ct);

        var response = await _client.GetAsync(
            $"/api/v1/tenant/agents/{agent}/secrets/token/versions", ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var body = await response.Content.ReadFromJsonAsync<SecretVersionsListResponse>(JsonOptions, ct);
        body.ShouldNotBeNull();
        body!.Versions.Count.ShouldBe(2);
    }

    private readonly record struct AgentFixture(Guid Id)
    {
        public string Path => Id.ToString("N");
        public override string ToString() => Path;
    }

    private static AgentFixture NewAgent() => new(Guid.NewGuid());

    private void StubAgent(AgentFixture a)
    {
        var address = new Address("agent", a.Id);
        var entry = new DirectoryEntry(address, a.Id, a.Path, "test", null, DateTimeOffset.UtcNow);
        _factory.DirectoryService.ResolveAsync(address, Arg.Any<CancellationToken>())
            .Returns(entry);
    }
}
