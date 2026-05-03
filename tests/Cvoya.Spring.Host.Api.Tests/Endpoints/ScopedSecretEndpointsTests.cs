// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Api.Tests.Endpoints;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

using Cvoya.Spring.Core.Secrets;
using Cvoya.Spring.Core.Tenancy;
using Cvoya.Spring.Dapr.Data;
using Cvoya.Spring.Dapr.Data.Entities;
using Cvoya.Spring.Host.Api.Endpoints;
using Cvoya.Spring.Host.Api.Models;

using Microsoft.Extensions.DependencyInjection;

using NSubstitute;
using NSubstitute.ClearExtensions;

using Shouldly;

using Xunit;

/// <summary>
/// HTTP-level tests for the tenant-scoped and platform-scoped secret
/// endpoints. These mirror the unit-scoped coverage in
/// <see cref="SecretEndpointsTests"/> for symmetry — every behavior
/// that matters at Unit scope (discriminated body, config flags,
/// origin-safe DELETE, access-policy hook, cross-tenant isolation)
/// is exercised for Tenant and Platform scope too.
///
/// <para>
/// The two scopes are parameterised via xUnit <see cref="TheoryAttribute"/>
/// data so every invariant is tested against both surfaces with a
/// single source of truth.
/// </para>
/// </summary>
public class ScopedSecretEndpointsTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;
    private readonly HttpClient _client;

    // Server uses JsonStringEnumConverter (Program.cs); tests must match.
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    public ScopedSecretEndpointsTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    public static IEnumerable<object[]> ScopedRoutes()
    {
        // (scope, basePath). OwnerId is derived per-test since tenant
        // secrets use ITenantContext.CurrentTenantId and platform secrets
        // use SecretEndpoints.PlatformOwnerId.
        yield return new object[] { SecretScope.Tenant, "/api/v1/tenant/secrets" };
        yield return new object[] { SecretScope.Platform, "/api/v1/platform/secrets" };
    }

    [Theory]
    [MemberData(nameof(ScopedRoutes))]
    public async Task Get_ReturnsEmpty_WhenNoSecrets(SecretScope scope, string basePath)
    {
        _ = scope;
        var ct = TestContext.Current.CancellationToken;
        await ClearSecretsAsync(ct);

        var response = await _client.GetAsync(basePath, ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var body = await response.Content.ReadFromJsonAsync<SecretsListResponse>(JsonOptions, ct);
        body.ShouldNotBeNull();
        body!.Secrets.ShouldBeEmpty();
    }

    [Theory]
    [MemberData(nameof(ScopedRoutes))]
    public async Task Post_PassThrough_Stores_And_Registers(SecretScope scope, string basePath)
    {
        var ct = TestContext.Current.CancellationToken;
        var name = NewName();
        await ClearSecretsAsync(ct);

        var response = await _client.PostAsJsonAsync(
            basePath, new CreateSecretRequest(name, "hunter2"), ct);
        response.StatusCode.ShouldBe(HttpStatusCode.Created);

        var body = await response.Content.ReadFromJsonAsync<CreateSecretResponse>(JsonOptions, ct);
        body.ShouldNotBeNull();
        body!.Name.ShouldBe(name);
        body.Scope.ShouldBe(scope);

        // Plaintext must never appear in any response body.
        var raw = await response.Content.ReadAsStringAsync(ct);
        raw.ShouldNotContain("hunter2");

        using var serviceScope = _factory.Services.CreateScope();
        var db = serviceScope.ServiceProvider.GetRequiredService<SpringDbContext>();
        var ownerId = ExpectedOwnerId(scope, serviceScope.ServiceProvider);
        var row = db.SecretRegistryEntries.SingleOrDefault(
            e => e.Scope == scope && e.OwnerId == ownerId && e.Name == name);
        row.ShouldNotBeNull();
        row!.Origin.ShouldBe(SecretOrigin.PlatformOwned);
        row.StoreKey.ShouldNotBeNullOrWhiteSpace();
        row.StoreKey.ShouldNotContain(ownerId);
        row.StoreKey.ShouldNotContain(name);
    }

    [Theory]
    [MemberData(nameof(ScopedRoutes))]
    public async Task Post_ExternalReference_Registers_WithoutStoreWrite(SecretScope scope, string basePath)
    {
        var ct = TestContext.Current.CancellationToken;
        var name = NewName();
        await ClearSecretsAsync(ct);
        _factory.SecretStore.ClearReceivedCalls();

        var response = await _client.PostAsJsonAsync(
            basePath,
            new CreateSecretRequest(name, ExternalStoreKey: "kv://vault1/secret1"),
            ct);
        response.StatusCode.ShouldBe(HttpStatusCode.Created);

        // External reference must NEVER trigger a store-layer write.
        await _factory.SecretStore.DidNotReceive()
            .WriteAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());

        using var serviceScope = _factory.Services.CreateScope();
        var db = serviceScope.ServiceProvider.GetRequiredService<SpringDbContext>();
        var ownerId = ExpectedOwnerId(scope, serviceScope.ServiceProvider);
        var row = db.SecretRegistryEntries.Single(
            e => e.Scope == scope && e.OwnerId == ownerId && e.Name == name);
        row.StoreKey.ShouldBe("kv://vault1/secret1");
        row.Origin.ShouldBe(SecretOrigin.ExternalReference);
    }

    [Theory]
    [MemberData(nameof(ScopedRoutes))]
    public async Task Post_BothValueAndExternal_Returns400_AsProblemDetails(SecretScope scope, string basePath)
    {
        _ = scope;
        var ct = TestContext.Current.CancellationToken;

        var response = await _client.PostAsJsonAsync(
            basePath, new CreateSecretRequest("both", "x", "kv://also"), ct);
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var raw = await response.Content.ReadAsStringAsync(ct);
        raw.ShouldContain("value");
    }

    [Theory]
    [MemberData(nameof(ScopedRoutes))]
    public async Task Post_NeitherValueNorExternal_Returns400(SecretScope scope, string basePath)
    {
        _ = scope;
        var ct = TestContext.Current.CancellationToken;

        var response = await _client.PostAsJsonAsync(
            basePath, new CreateSecretRequest("neither"), ct);
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Theory]
    [MemberData(nameof(ScopedRoutes))]
    public async Task Post_EmptyName_Returns400(SecretScope scope, string basePath)
    {
        _ = scope;
        var ct = TestContext.Current.CancellationToken;

        var response = await _client.PostAsJsonAsync(
            basePath, new CreateSecretRequest("", "x"), ct);
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Theory]
    [MemberData(nameof(ScopedRoutes))]
    public async Task Delete_ExistingSecret_Returns204_RemovesRegistryRow(SecretScope scope, string basePath)
    {
        var ct = TestContext.Current.CancellationToken;
        var name = NewName();
        await ClearSecretsAsync(ct);

        var postResponse = await _client.PostAsJsonAsync(
            basePath, new CreateSecretRequest(name, ExternalStoreKey: "kv://vault/x"), ct);
        postResponse.StatusCode.ShouldBe(HttpStatusCode.Created);

        var deleteResponse = await _client.DeleteAsync($"{basePath}/{name}", ct);
        deleteResponse.StatusCode.ShouldBe(HttpStatusCode.NoContent);

        using var serviceScope = _factory.Services.CreateScope();
        var db = serviceScope.ServiceProvider.GetRequiredService<SpringDbContext>();
        var ownerId = ExpectedOwnerId(scope, serviceScope.ServiceProvider);
        db.SecretRegistryEntries
            .Where(e => e.Scope == scope && e.OwnerId == ownerId && e.Name == name)
            .ShouldBeEmpty();
    }

    [Theory]
    [MemberData(nameof(ScopedRoutes))]
    public async Task Delete_MissingSecret_Returns404_AsProblemDetails(SecretScope scope, string basePath)
    {
        _ = scope;
        var ct = TestContext.Current.CancellationToken;
        await ClearSecretsAsync(ct);

        var response = await _client.DeleteAsync($"{basePath}/{NewName()}", ct);
        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        response.Content.Headers.ContentType?.MediaType
            .ShouldBe("application/problem+json");
    }

    [Theory]
    [MemberData(nameof(ScopedRoutes))]
    public async Task Delete_PlatformOwnedSecret_CallsStoreDelete(SecretScope scope, string basePath)
    {
        _ = scope;
        var ct = TestContext.Current.CancellationToken;
        var name = NewName();
        await ClearSecretsAsync(ct);
        _factory.SecretStore.ClearReceivedCalls();

        var postResponse = await _client.PostAsJsonAsync(
            basePath, new CreateSecretRequest(name, "hunter2"), ct);
        postResponse.StatusCode.ShouldBe(HttpStatusCode.Created);

        string storeKey;
        using (var serviceScope = _factory.Services.CreateScope())
        {
            var db = serviceScope.ServiceProvider.GetRequiredService<SpringDbContext>();
            var ownerId = ExpectedOwnerId(scope, serviceScope.ServiceProvider);
            storeKey = db.SecretRegistryEntries
                .Single(e => e.Scope == scope && e.OwnerId == ownerId && e.Name == name)
                .StoreKey;
        }

        var deleteResponse = await _client.DeleteAsync($"{basePath}/{name}", ct);
        deleteResponse.StatusCode.ShouldBe(HttpStatusCode.NoContent);

        await _factory.SecretStore.Received(1)
            .DeleteAsync(storeKey, Arg.Any<CancellationToken>());
    }

    [Theory]
    [MemberData(nameof(ScopedRoutes))]
    public async Task Delete_ExternalReferenceSecret_DoesNotCallStoreDelete(SecretScope scope, string basePath)
    {
        _ = scope;
        var ct = TestContext.Current.CancellationToken;
        var name = NewName();
        await ClearSecretsAsync(ct);
        _factory.SecretStore.ClearReceivedCalls();

        var postResponse = await _client.PostAsJsonAsync(
            basePath,
            new CreateSecretRequest(name, ExternalStoreKey: "kv://vault/ext"),
            ct);
        postResponse.StatusCode.ShouldBe(HttpStatusCode.Created);

        var deleteResponse = await _client.DeleteAsync($"{basePath}/{name}", ct);
        deleteResponse.StatusCode.ShouldBe(HttpStatusCode.NoContent);

        await _factory.SecretStore.DidNotReceive()
            .DeleteAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(SecretScope.Tenant, "/api/v1/tenant/secrets", SecretAccessAction.List)]
    [InlineData(SecretScope.Tenant, "/api/v1/tenant/secrets", SecretAccessAction.Create)]
    [InlineData(SecretScope.Tenant, "/api/v1/tenant/secrets", SecretAccessAction.Delete)]
    [InlineData(SecretScope.Platform, "/api/v1/platform/secrets", SecretAccessAction.List)]
    [InlineData(SecretScope.Platform, "/api/v1/platform/secrets", SecretAccessAction.Create)]
    [InlineData(SecretScope.Platform, "/api/v1/platform/secrets", SecretAccessAction.Delete)]
    public async Task Returns403_WhenAccessPolicyDeniesAction(SecretScope scope, string basePath, SecretAccessAction action)
    {
        var ct = TestContext.Current.CancellationToken;
        _factory.SecretAccessPolicy.ClearSubstitute();
        _factory.SecretAccessPolicy
            .IsAuthorizedAsync(action, scope, Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(false));
        _factory.SecretAccessPolicy
            .IsAuthorizedAsync(
                Arg.Is<SecretAccessAction>(a => a != action),
                Arg.Any<SecretScope>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(true));

        try
        {
            HttpResponseMessage response = action switch
            {
                SecretAccessAction.List => await _client.GetAsync(basePath, ct),
                SecretAccessAction.Create => await _client.PostAsJsonAsync(
                    basePath, new CreateSecretRequest("denied", "x"), ct),
                SecretAccessAction.Delete => await _client.DeleteAsync($"{basePath}/whatever", ct),
                _ => throw new InvalidOperationException(),
            };

            response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
            response.Content.Headers.ContentType?.MediaType
                .ShouldBe("application/problem+json");
        }
        finally
        {
            // Reset to permissive default for other tests in the shared fixture.
            _factory.SecretAccessPolicy.ClearSubstitute();
            _factory.SecretAccessPolicy
                .IsAuthorizedAsync(
                    Arg.Any<SecretAccessAction>(),
                    Arg.Any<SecretScope>(),
                    Arg.Any<string>(),
                    Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(true));
        }
    }

    [Theory]
    [MemberData(nameof(ScopedRoutes))]
    public async Task Post_PassThroughDisabled_Returns403(SecretScope scope, string basePath)
    {
        _ = scope;
        var ct = TestContext.Current.CancellationToken;
        // The factory binds SecretsOptions from configuration; the test
        // host's default configuration allows both write modes. Flip the
        // switch off by re-binding via request headers is impossible, so
        // instead exercise the validator directly by posting with BOTH
        // to guarantee 400, and by posting with value-only while mutating
        // the singleton options via a scope.
        var opts = _factory.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<Cvoya.Spring.Dapr.Tenancy.SecretsOptions>>();
        var current = opts.Value;
        var originalPassThrough = current.AllowPassThroughWrites;
        current.AllowPassThroughWrites = false;
        try
        {
            var response = await _client.PostAsJsonAsync(
                basePath, new CreateSecretRequest(NewName(), "x"), ct);
            response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
            response.Content.Headers.ContentType?.MediaType
                .ShouldBe("application/problem+json");
        }
        finally
        {
            current.AllowPassThroughWrites = originalPassThrough;
        }
    }

    [Theory]
    [MemberData(nameof(ScopedRoutes))]
    public async Task Post_ExternalReferenceDisabled_Returns403(SecretScope scope, string basePath)
    {
        _ = scope;
        var ct = TestContext.Current.CancellationToken;
        var opts = _factory.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<Cvoya.Spring.Dapr.Tenancy.SecretsOptions>>();
        var current = opts.Value;
        var originalExternal = current.AllowExternalReferenceWrites;
        current.AllowExternalReferenceWrites = false;
        try
        {
            var response = await _client.PostAsJsonAsync(
                basePath,
                new CreateSecretRequest(NewName(), ExternalStoreKey: "kv://v/x"),
                ct);
            response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        }
        finally
        {
            current.AllowExternalReferenceWrites = originalExternal;
        }
    }

    [Theory]
    [MemberData(nameof(ScopedRoutes))]
    public async Task Get_CrossTenantIsolation_DoesNotLeakOtherTenantEntries(SecretScope scope, string basePath)
    {
        var ct = TestContext.Current.CancellationToken;
        var name = NewName();
        await ClearSecretsAsync(ct);

        using (var serviceScope = _factory.Services.CreateScope())
        {
            var db = serviceScope.ServiceProvider.GetRequiredService<SpringDbContext>();
            var ownerId = ExpectedOwnerId(scope, serviceScope.ServiceProvider);

            // Seed a row owned by a DIFFERENT tenant that shares the
            // same (scope, owner, name) triple. The scoped endpoints
            // run under the factory's default tenant "local"; a foreign
            // tenant's entries must not leak through the list surface.
            db.SecretRegistryEntries.Add(new SecretRegistryEntry
            {
                Id = Guid.NewGuid(),
                TenantId = "other-tenant",
                Scope = scope,
                OwnerId = ownerId,
                DisplayName = name,
                StoreKey = "sk-other",
                Origin = SecretOrigin.PlatformOwned,
                CreatedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync(ct);
        }

        var response = await _client.GetAsync(basePath, ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var body = await response.Content.ReadFromJsonAsync<SecretsListResponse>(JsonOptions, ct);
        body!.Secrets.ShouldNotContain(s => s.Name == name);
    }

    private static string NewName() => $"secret-{Guid.NewGuid():N}";

    private static string ExpectedOwnerId(SecretScope scope, IServiceProvider sp) => scope switch
    {
        SecretScope.Tenant => sp.GetRequiredService<ITenantContext>().CurrentTenantId,
        SecretScope.Platform => SecretEndpoints.PlatformOwnerId,
        _ => throw new ArgumentOutOfRangeException(nameof(scope), scope, "Only Tenant/Platform are covered by this fixture."),
    };

    // The tenant- and platform-scoped tests share the factory's
    // in-memory database. Clear the two scopes' rows at the start of
    // each test so state from a prior test can't change 404/empty-list
    // assertions.
    private async Task ClearSecretsAsync(CancellationToken ct)
    {
        using var serviceScope = _factory.Services.CreateScope();
        var db = serviceScope.ServiceProvider.GetRequiredService<SpringDbContext>();
        var rows = db.SecretRegistryEntries
            .Where(e => e.Scope == SecretScope.Tenant || e.Scope == SecretScope.Platform)
            .ToList();
        if (rows.Count > 0)
        {
            db.SecretRegistryEntries.RemoveRange(rows);
            await db.SaveChangesAsync(ct);
        }
    }
}