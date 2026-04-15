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
using NSubstitute.ClearExtensions;

using Shouldly;

using Xunit;

/// <summary>
/// HTTP-level tests for the rotation endpoints (#201). Exercises the
/// unit-, tenant-, and platform-scoped <c>PUT /secrets/{name}</c>
/// surfaces introduced in wave 5 A4. The rotation endpoints are kept
/// on a dedicated fixture so the existing create/delete tests can
/// continue to share state without rotation-path mutations leaking in.
/// </summary>
public class SecretRotationEndpointsTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;
    private readonly HttpClient _client;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    public SecretRotationEndpointsTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    public static IEnumerable<object[]> ScopedRoutes()
    {
        yield return new object[] { SecretScope.Unit, "unit-scoped" };
        yield return new object[] { SecretScope.Tenant, "tenant-scoped" };
        yield return new object[] { SecretScope.Platform, "platform-scoped" };
    }

    [Theory]
    [MemberData(nameof(ScopedRoutes))]
    public async Task Put_RotatesPlatformOwnedSecret_AppendsNewVersion_RetainsOldSlot(SecretScope scope, string _label)
    {
        // A5 multi-version: rotate APPENDS a row at max(Version)+1 and
        // leaves the old store-layer slot in place so pinned resolves
        // still work. The endpoint now returns 200 + the new version.
        _ = _label;
        var ct = TestContext.Current.CancellationToken;
        var ctx = await SeedAsync(scope, passThrough: true, ct);
        _factory.SecretStore.ClearReceivedCalls();

        var response = await _client.PutAsJsonAsync(
            $"{ctx.BasePath}/{ctx.Name}",
            new RotateSecretRequest(Value: "new-value"),
            ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<RotateSecretResponse>(JsonOptions, ct);
        body.ShouldNotBeNull();
        body!.Version.ShouldBe(2);
        body.Name.ShouldBe(ctx.Name);

        // Rotation must NOT delete the prior platform-owned slot under
        // the multi-version retention policy.
        await _factory.SecretStore.DidNotReceive()
            .DeleteAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());

        // A fresh WriteAsync was made for the new plaintext.
        await _factory.SecretStore.Received(1)
            .WriteAsync("new-value", Arg.Any<CancellationToken>());

        // Both versions now coexist in the registry.
        using var serviceScope = _factory.Services.CreateScope();
        var db = serviceScope.ServiceProvider.GetRequiredService<SpringDbContext>();
        var rows = db.SecretRegistryEntries
            .Where(e => e.Scope == scope && e.OwnerId == ctx.OwnerId && e.Name == ctx.Name)
            .OrderBy(e => e.Version)
            .ToList();
        rows.Count.ShouldBe(2);
        rows[0].Version.ShouldBe(1);
        rows[0].StoreKey.ShouldBe(ctx.OriginalStoreKey);
        rows[1].Version.ShouldBe(2);
        rows[1].Origin.ShouldBe(SecretOrigin.PlatformOwned);
        rows[1].StoreKey.ShouldNotBe(ctx.OriginalStoreKey);
    }

    [Theory]
    [MemberData(nameof(ScopedRoutes))]
    public async Task Put_RotatesExternalReferenceSecret_AppendsNewVersion_DoesNotCallStore(SecretScope scope, string _label)
    {
        _ = _label;
        var ct = TestContext.Current.CancellationToken;
        var ctx = await SeedAsync(scope, passThrough: false, ct);
        _factory.SecretStore.ClearReceivedCalls();

        var response = await _client.PutAsJsonAsync(
            $"{ctx.BasePath}/{ctx.Name}",
            new RotateSecretRequest(ExternalStoreKey: "kv://vault/new"),
            ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<RotateSecretResponse>(JsonOptions, ct);
        body!.Version.ShouldBe(2);

        // External-reference rotations must NEVER touch the backing
        // store — the customer owns all versions.
        await _factory.SecretStore.DidNotReceive()
            .DeleteAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _factory.SecretStore.DidNotReceive()
            .WriteAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());

        using var serviceScope = _factory.Services.CreateScope();
        var db = serviceScope.ServiceProvider.GetRequiredService<SpringDbContext>();
        var rows = db.SecretRegistryEntries
            .Where(e => e.Scope == scope && e.OwnerId == ctx.OwnerId && e.Name == ctx.Name)
            .OrderBy(e => e.Version)
            .ToList();
        rows.Count.ShouldBe(2);
        rows[1].StoreKey.ShouldBe("kv://vault/new");
        rows[1].Origin.ShouldBe(SecretOrigin.ExternalReference);
    }

    [Theory]
    [MemberData(nameof(ScopedRoutes))]
    public async Task Put_OriginFlip_PlatformToExternal_RetainsOldPlatformSlot(SecretScope scope, string _label)
    {
        // A5 multi-version: an origin flip appends a new row; the
        // prior platform-owned slot is retained (prune/delete is the
        // explicit path to reclaim it).
        _ = _label;
        var ct = TestContext.Current.CancellationToken;
        var ctx = await SeedAsync(scope, passThrough: true, ct);
        _factory.SecretStore.ClearReceivedCalls();

        var response = await _client.PutAsJsonAsync(
            $"{ctx.BasePath}/{ctx.Name}",
            new RotateSecretRequest(ExternalStoreKey: "kv://vault/ext"),
            ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        // Delete must NOT have been called under the retention policy.
        await _factory.SecretStore.DidNotReceive()
            .DeleteAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());

        using var serviceScope = _factory.Services.CreateScope();
        var db = serviceScope.ServiceProvider.GetRequiredService<SpringDbContext>();
        var rows = db.SecretRegistryEntries
            .Where(e => e.Scope == scope && e.OwnerId == ctx.OwnerId && e.Name == ctx.Name)
            .OrderBy(e => e.Version)
            .ToList();
        rows.Count.ShouldBe(2);
        rows[0].StoreKey.ShouldBe(ctx.OriginalStoreKey);
        rows[0].Origin.ShouldBe(SecretOrigin.PlatformOwned);
        rows[1].StoreKey.ShouldBe("kv://vault/ext");
        rows[1].Origin.ShouldBe(SecretOrigin.ExternalReference);
    }

    [Theory]
    [MemberData(nameof(ScopedRoutes))]
    public async Task Put_MissingSecret_Returns404(SecretScope scope, string _label)
    {
        _ = _label;
        var ct = TestContext.Current.CancellationToken;
        var (basePath, _ownerId) = ResolveRoute(scope);
        _ = _ownerId;

        // Unit scope needs a valid unit for the POST, but the rotation
        // PUT goes straight to the registry without a unit lookup —
        // a missing entry is enough.
        if (scope == SecretScope.Unit)
        {
            StubUnit(basePath.Split('/').Last().Replace("secrets", "").Trim('/'));
        }

        var response = await _client.PutAsJsonAsync(
            $"{basePath}/does-not-exist-{Guid.NewGuid():N}",
            new RotateSecretRequest(Value: "x"),
            ct);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        response.Content.Headers.ContentType?.MediaType
            .ShouldBe("application/problem+json");
    }

    [Theory]
    [MemberData(nameof(ScopedRoutes))]
    public async Task Put_BothValueAndExternal_Returns400(SecretScope scope, string _label)
    {
        _ = _label;
        var ct = TestContext.Current.CancellationToken;
        var ctx = await SeedAsync(scope, passThrough: true, ct);

        var response = await _client.PutAsJsonAsync(
            $"{ctx.BasePath}/{ctx.Name}",
            new RotateSecretRequest(Value: "x", ExternalStoreKey: "kv://y"),
            ct);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Theory]
    [MemberData(nameof(ScopedRoutes))]
    public async Task Put_NeitherValueNorExternal_Returns400(SecretScope scope, string _label)
    {
        _ = _label;
        var ct = TestContext.Current.CancellationToken;
        var ctx = await SeedAsync(scope, passThrough: true, ct);

        var response = await _client.PutAsJsonAsync(
            $"{ctx.BasePath}/{ctx.Name}",
            new RotateSecretRequest(),
            ct);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Theory]
    [MemberData(nameof(ScopedRoutes))]
    public async Task Put_Returns403_WhenAccessPolicyDeniesRotate(SecretScope scope, string _label)
    {
        _ = _label;
        var ct = TestContext.Current.CancellationToken;
        var ctx = await SeedAsync(scope, passThrough: true, ct);

        _factory.SecretAccessPolicy.ClearReceivedCalls();
        _factory.SecretAccessPolicy
            .IsAuthorizedAsync(SecretAccessAction.Rotate, scope, Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(false));

        try
        {
            var response = await _client.PutAsJsonAsync(
                $"{ctx.BasePath}/{ctx.Name}",
                new RotateSecretRequest(Value: "x"),
                ct);

            response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
            response.Content.Headers.ContentType?.MediaType
                .ShouldBe("application/problem+json");
        }
        finally
        {
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
    public async Task Put_PassThroughDisabled_Returns403(SecretScope scope, string _label)
    {
        _ = _label;
        var ct = TestContext.Current.CancellationToken;
        var ctx = await SeedAsync(scope, passThrough: true, ct);

        var opts = _factory.Services.GetRequiredService<
            Microsoft.Extensions.Options.IOptions<Cvoya.Spring.Dapr.Tenancy.SecretsOptions>>();
        var originalPassThrough = opts.Value.AllowPassThroughWrites;
        opts.Value.AllowPassThroughWrites = false;
        try
        {
            var response = await _client.PutAsJsonAsync(
                $"{ctx.BasePath}/{ctx.Name}",
                new RotateSecretRequest(Value: "x"),
                ct);

            response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        }
        finally
        {
            opts.Value.AllowPassThroughWrites = originalPassThrough;
        }
    }

    [Theory]
    [MemberData(nameof(ScopedRoutes))]
    public async Task Put_ExternalDisabled_Returns403(SecretScope scope, string _label)
    {
        _ = _label;
        var ct = TestContext.Current.CancellationToken;
        var ctx = await SeedAsync(scope, passThrough: false, ct);

        var opts = _factory.Services.GetRequiredService<
            Microsoft.Extensions.Options.IOptions<Cvoya.Spring.Dapr.Tenancy.SecretsOptions>>();
        var original = opts.Value.AllowExternalReferenceWrites;
        opts.Value.AllowExternalReferenceWrites = false;
        try
        {
            var response = await _client.PutAsJsonAsync(
                $"{ctx.BasePath}/{ctx.Name}",
                new RotateSecretRequest(ExternalStoreKey: "kv://new"),
                ct);

            response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        }
        finally
        {
            opts.Value.AllowExternalReferenceWrites = original;
        }
    }

    // ----- test infrastructure -----

    private record SeedContext(
        string Name,
        string BasePath,
        string OwnerId,
        string? OriginalStoreKey,
        DateTimeOffset OriginalUpdatedAt);

    private async Task<SeedContext> SeedAsync(SecretScope scope, bool passThrough, CancellationToken ct)
    {
        var name = $"secret-{Guid.NewGuid():N}";
        var (basePath, ownerId) = ResolveRoute(scope);

        if (scope == SecretScope.Unit)
        {
            StubUnit(ownerId);
        }

        // Seed an existing entry via POST so both the registry row and
        // any platform-owned store-layer slot exist before the rotate.
        CreateSecretRequest request = passThrough
            ? new(name, Value: "original-plaintext")
            : new(name, ExternalStoreKey: "kv://vault/original");

        var postResponse = await _client.PostAsJsonAsync(basePath, request, ct);
        postResponse.StatusCode.ShouldBe(HttpStatusCode.Created);

        using var serviceScope = _factory.Services.CreateScope();
        var db = serviceScope.ServiceProvider.GetRequiredService<SpringDbContext>();
        var row = db.SecretRegistryEntries.Single(
            e => e.Scope == scope && e.OwnerId == ownerId && e.Name == name);
        return new SeedContext(name, basePath, ownerId, row.StoreKey, row.UpdatedAt);
    }

    private (string BasePath, string OwnerId) ResolveRoute(SecretScope scope)
    {
        switch (scope)
        {
            case SecretScope.Unit:
                var unitId = $"unit-{Guid.NewGuid():N}";
                return ($"/api/v1/units/{unitId}/secrets", unitId);
            case SecretScope.Tenant:
                using (var svcScope = _factory.Services.CreateScope())
                {
                    var tenant = svcScope.ServiceProvider.GetRequiredService<ITenantContext>().CurrentTenantId;
                    return ("/api/v1/tenant/secrets", tenant);
                }
            case SecretScope.Platform:
                return ("/api/v1/platform/secrets", Cvoya.Spring.Host.Api.Endpoints.SecretEndpoints.PlatformOwnerId);
            default:
                throw new ArgumentOutOfRangeException(nameof(scope));
        }
    }

    private void StubUnit(string id)
    {
        var address = new Address("unit", id);
        var entry = new DirectoryEntry(
            address, id, id, "test", null, DateTimeOffset.UtcNow);
        _factory.DirectoryService.ResolveAsync(address, Arg.Any<CancellationToken>())
            .Returns(entry);
    }
}