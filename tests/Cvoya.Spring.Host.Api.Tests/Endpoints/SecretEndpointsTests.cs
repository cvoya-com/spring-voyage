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
using Cvoya.Spring.Dapr.Data.Entities;
using Cvoya.Spring.Host.Api.Models;

using Microsoft.Extensions.DependencyInjection;

using NSubstitute;

using Shouldly;

using Xunit;

/// <summary>
/// HTTP-level tests for the unit-scoped secret endpoints. Uses the
/// shared <see cref="CustomWebApplicationFactory"/> which wires an
/// in-memory EF database and a mocked <see cref="IDirectoryService"/>.
/// The resident <see cref="ITenantContext"/> registered by the factory
/// resolves to <c>"local"</c>; tests that need to exercise cross-tenant
/// isolation seed rows under a different tenant id directly.
/// </summary>
public class SecretEndpointsTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;
    private readonly HttpClient _client;

    // Server uses JsonStringEnumConverter (Program.cs); tests must match.
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    public SecretEndpointsTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
        StubUnit(U1);
    }

    [Fact]
    public async Task Get_Returns404_WhenUnitMissing()
    {
        var ct = TestContext.Current.CancellationToken;
        var missingId = Guid.NewGuid();
        _factory.DirectoryService.ResolveAsync(
            new Address("unit", missingId), Arg.Any<CancellationToken>())
            .Returns((DirectoryEntry?)null);

        var response = await _client.GetAsync($"/api/v1/tenant/units/{missingId:N}/secrets", ct);
        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Get_ReturnsEmpty_WhenNoSecrets()
    {
        var ct = TestContext.Current.CancellationToken;

        var response = await _client.GetAsync($"/api/v1/tenant/units/{U1}/secrets", ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var body = await response.Content.ReadFromJsonAsync<UnitSecretsListResponse>(JsonOptions, ct);
        body.ShouldNotBeNull();
        body!.Secrets.ShouldBeEmpty();
    }

    [Fact]
    public async Task Post_PassThrough_Stores_And_Registers()
    {
        var ct = TestContext.Current.CancellationToken;
        var unit = NewUnit();
        StubUnit(unit);

        var response = await _client.PostAsJsonAsync(
            $"/api/v1/tenant/units/{unit}/secrets",
            new CreateSecretRequest("gh-token", "ghp_abc123"),
            ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Created);

        var body = await response.Content.ReadFromJsonAsync<CreateSecretResponse>(JsonOptions, ct);
        body.ShouldNotBeNull();
        body!.Name.ShouldBe("gh-token");
        body.Scope.ShouldBe(SecretScope.Unit);

        // Plaintext must never appear in any response body.
        var raw = await response.Content.ReadAsStringAsync(ct);
        raw.ShouldNotContain("ghp_abc123");

        // A registry row now exists for the current tenant.
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SpringDbContext>();
        var tenant = scope.ServiceProvider.GetRequiredService<ITenantContext>().CurrentTenantId;
        var row = db.SecretRegistryEntries.SingleOrDefault(
            e => e.TenantId == tenant && e.OwnerId == unit.Id && e.Name == "gh-token");
        row.ShouldNotBeNull();
        row!.StoreKey.ShouldNotBeNullOrWhiteSpace();
        // StoreKey must be opaque: it must not encode the tenant,
        // unit, or secret name.
        row.StoreKey.ShouldNotContain(tenant.ToString("N"));
        row.StoreKey.ShouldNotContain(unit.Path);
        row.StoreKey.ShouldNotContain("gh-token");
    }

    [Fact]
    public async Task Post_ExternalReference_Registers_WithoutStoreWrite()
    {
        var ct = TestContext.Current.CancellationToken;
        var unit = NewUnit();
        StubUnit(unit);

        var response = await _client.PostAsJsonAsync(
            $"/api/v1/tenant/units/{unit}/secrets",
            new CreateSecretRequest("kv-ref", ExternalStoreKey: "kv://vault1/secret1"),
            ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Created);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SpringDbContext>();
        var tenant = scope.ServiceProvider.GetRequiredService<ITenantContext>().CurrentTenantId;
        var row = db.SecretRegistryEntries.Single(
            e => e.TenantId == tenant && e.OwnerId == unit.Id && e.Name == "kv-ref");
        row.StoreKey.ShouldBe("kv://vault1/secret1");
    }

    [Fact]
    public async Task Post_BothValueAndExternal_Returns400()
    {
        var ct = TestContext.Current.CancellationToken;
        var unit = NewUnit();
        StubUnit(unit);

        var response = await _client.PostAsJsonAsync(
            $"/api/v1/tenant/units/{unit}/secrets",
            new CreateSecretRequest("both", "x", "kv://also"),
            ct);
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Post_NeitherValueNorExternal_Returns400()
    {
        var ct = TestContext.Current.CancellationToken;
        var unit = NewUnit();
        StubUnit(unit);

        var response = await _client.PostAsJsonAsync(
            $"/api/v1/tenant/units/{unit}/secrets",
            new CreateSecretRequest("neither"),
            ct);
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Post_EmptyName_Returns400()
    {
        var ct = TestContext.Current.CancellationToken;
        var unit = NewUnit();
        StubUnit(unit);

        var response = await _client.PostAsJsonAsync(
            $"/api/v1/tenant/units/{unit}/secrets",
            new CreateSecretRequest("", "x"),
            ct);
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Delete_ExistingSecret_Returns204_RemovesRegistryRow()
    {
        var ct = TestContext.Current.CancellationToken;
        var unit = NewUnit();
        StubUnit(unit);

        var postResponse = await _client.PostAsJsonAsync(
            $"/api/v1/tenant/units/{unit}/secrets",
            new CreateSecretRequest("temp", ExternalStoreKey: "kv://vault/x"),
            ct);
        postResponse.StatusCode.ShouldBe(HttpStatusCode.Created);

        var deleteResponse = await _client.DeleteAsync(
            $"/api/v1/tenant/units/{unit}/secrets/temp", ct);
        deleteResponse.StatusCode.ShouldBe(HttpStatusCode.NoContent);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SpringDbContext>();
        db.SecretRegistryEntries
            .Where(e => e.OwnerId == unit.Id && e.Name == "temp")
            .ShouldBeEmpty();
    }

    [Fact]
    public async Task Delete_PlatformOwnedSecret_CallsStoreDelete()
    {
        // The DELETE path must mutate the backing store slot when the
        // platform owns it — otherwise pass-through writes would leak
        // plaintext via ISecretStore forever.
        var ct = TestContext.Current.CancellationToken;
        var unit = NewUnit();
        StubUnit(unit);
        _factory.SecretStore.ClearReceivedCalls();

        var postResponse = await _client.PostAsJsonAsync(
            $"/api/v1/tenant/units/{unit}/secrets",
            new CreateSecretRequest("owned", "hunter2"),
            ct);
        postResponse.StatusCode.ShouldBe(HttpStatusCode.Created);

        // Capture the opaque store key the stub returned, then DELETE.
        string? storeKey;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<SpringDbContext>();
            var tenant = scope.ServiceProvider.GetRequiredService<ITenantContext>().CurrentTenantId;
            storeKey = db.SecretRegistryEntries
                .Single(e => e.TenantId == tenant && e.OwnerId == unit.Id && e.Name == "owned")
                .StoreKey;
        }

        var deleteResponse = await _client.DeleteAsync(
            $"/api/v1/tenant/units/{unit}/secrets/owned", ct);
        deleteResponse.StatusCode.ShouldBe(HttpStatusCode.NoContent);

        await _factory.SecretStore.Received(1)
            .DeleteAsync(storeKey!, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Delete_ExternalReferenceSecret_DoesNotCallStoreDelete()
    {
        // CRITICAL invariant: DELETE on an external-reference entry must
        // remove the registry row ONLY. Calling ISecretStore.DeleteAsync
        // on an externally-managed key would destroy a customer-owned
        // secret in the private-cloud Key Vault implementation.
        var ct = TestContext.Current.CancellationToken;
        var unit = NewUnit();
        StubUnit(unit);
        _factory.SecretStore.ClearReceivedCalls();

        var postResponse = await _client.PostAsJsonAsync(
            $"/api/v1/tenant/units/{unit}/secrets",
            new CreateSecretRequest("ext", ExternalStoreKey: "kv://vault1/secret1"),
            ct);
        postResponse.StatusCode.ShouldBe(HttpStatusCode.Created);

        var deleteResponse = await _client.DeleteAsync(
            $"/api/v1/tenant/units/{unit}/secrets/ext", ct);
        deleteResponse.StatusCode.ShouldBe(HttpStatusCode.NoContent);

        await _factory.SecretStore.DidNotReceive()
            .DeleteAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());

        // Registry row is gone; the external key remains wherever the
        // caller manages it.
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SpringDbContext>();
        db.SecretRegistryEntries
            .Where(e => e.OwnerId == unit.Id && e.Name == "ext")
            .ShouldBeEmpty();
    }

    [Fact]
    public async Task Post_PassThrough_Records_PlatformOwnedOrigin()
    {
        var ct = TestContext.Current.CancellationToken;
        var unit = NewUnit();
        StubUnit(unit);

        var response = await _client.PostAsJsonAsync(
            $"/api/v1/tenant/units/{unit}/secrets",
            new CreateSecretRequest("owned", "hunter2"),
            ct);
        response.StatusCode.ShouldBe(HttpStatusCode.Created);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SpringDbContext>();
        var row = db.SecretRegistryEntries.Single(
            e => e.OwnerId == unit.Id && e.Name == "owned");
        row.Origin.ShouldBe(SecretOrigin.PlatformOwned);
    }

    [Fact]
    public async Task Post_ExternalReference_Records_ExternalReferenceOrigin()
    {
        var ct = TestContext.Current.CancellationToken;
        var unit = NewUnit();
        StubUnit(unit);

        var response = await _client.PostAsJsonAsync(
            $"/api/v1/tenant/units/{unit}/secrets",
            new CreateSecretRequest("ext", ExternalStoreKey: "kv://vault/x"),
            ct);
        response.StatusCode.ShouldBe(HttpStatusCode.Created);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SpringDbContext>();
        var row = db.SecretRegistryEntries.Single(
            e => e.OwnerId == unit.Id && e.Name == "ext");
        row.Origin.ShouldBe(SecretOrigin.ExternalReference);
    }

    [Fact]
    public async Task List_Returns403_WhenAccessPolicyDenies()
    {
        var ct = TestContext.Current.CancellationToken;
        var unit = NewUnit();
        StubUnit(unit);

        _factory.SecretAccessPolicy
            .IsAuthorizedAsync(SecretAccessAction.List, SecretScope.Unit, unit.Id, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(false));

        var response = await _client.GetAsync($"/api/v1/tenant/units/{unit}/secrets", ct);
        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        // Reset for other tests running against the same factory.
        _factory.SecretAccessPolicy
            .IsAuthorizedAsync(SecretAccessAction.List, SecretScope.Unit, unit.Id, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(true));
    }

    [Fact]
    public async Task Delete_MissingSecret_Returns404()
    {
        var ct = TestContext.Current.CancellationToken;
        var unit = NewUnit();
        StubUnit(unit);

        var response = await _client.DeleteAsync(
            $"/api/v1/tenant/units/{unit}/secrets/nope", ct);
        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Get_CrossTenantIsolation_DoesNotLeakOtherTenantEntries()
    {
        // Seed a row owned by a DIFFERENT tenant directly in the
        // database, then verify the list endpoint (which runs under
        // the factory's default tenant "local") does not see it.
        var ct = TestContext.Current.CancellationToken;
        var unit = NewUnit();
        StubUnit(unit);

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<SpringDbContext>();
            db.SecretRegistryEntries.Add(new SecretRegistryEntry
            {
                Id = Guid.NewGuid(),
                TenantId = Guid.NewGuid(),
                Scope = SecretScope.Unit,
                OwnerId = unit.Id,
                Name = "leaked",
                StoreKey = "sk-other",
                CreatedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync(ct);
        }

        var response = await _client.GetAsync($"/api/v1/tenant/units/{unit}/secrets", ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var body = await response.Content.ReadFromJsonAsync<UnitSecretsListResponse>(JsonOptions, ct);
        body!.Secrets.ShouldNotContain(s => s.Name == "leaked");
    }

    // Post #1629: there are no slugs, so the slug-vs-uuid collision the
    // #1488 test exercised is structurally impossible — every unit's
    // identity is a fresh Guid. The test was deleted as obsolete.

    /// <summary>
    /// Test unit fixture: a stable Guid identity surfaced as both the URL
    /// path segment (no-dash hex) and the database OwnerId (Guid).
    /// </summary>
    private readonly record struct UnitFixture(Guid Id)
    {
        public string Path => Id.ToString("N");
        public override string ToString() => Path;
    }

    private static UnitFixture NewUnit() => new(Guid.NewGuid());

    private static readonly UnitFixture U1 = new(new Guid("aaaaaaaa-1111-1111-1111-000000000001"));

    private void StubUnit(UnitFixture u)
    {
        var address = new Address("unit", u.Id);
        var entry = new DirectoryEntry(
            address, u.Id, u.Path, "test", null, DateTimeOffset.UtcNow);
        _factory.DirectoryService.ResolveAsync(address, Arg.Any<CancellationToken>())
            .Returns(entry);
    }
}