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
/// HTTP-level tests for the new wave 7 A5 multi-version endpoints:
/// <c>GET /api/v1/.../secrets/{name}/versions</c> and
/// <c>POST /api/v1/.../secrets/{name}/prune</c>. Mirrors the existing
/// rotation-test pattern (per-scope theory cases, shared fixture).
/// </summary>
public class SecretMultiVersionEndpointsTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;
    private readonly HttpClient _client;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    public SecretMultiVersionEndpointsTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    public static IEnumerable<object[]> ScopedRoutes()
    {
        yield return new object[] { SecretScope.Unit, "unit" };
        yield return new object[] { SecretScope.Tenant, "tenant" };
        yield return new object[] { SecretScope.Platform, "platform" };
    }

    // -----------------------------------------------------------------
    // GET /versions
    // -----------------------------------------------------------------

    [Theory]
    [MemberData(nameof(ScopedRoutes))]
    public async Task GetVersions_AfterTwoRotations_ReturnsThreeRows_NewestFirst(SecretScope scope, string _label)
    {
        _ = _label;
        var ct = TestContext.Current.CancellationToken;
        var ctx = await SeedAsync(scope, passThrough: true, ct);

        // Two rotations after the initial POST -> v1, v2, v3.
        await RotateAsync(ctx, "v2", ct);
        await RotateAsync(ctx, "v3", ct);

        var response = await _client.GetAsync($"{ctx.BasePath}/{ctx.Name}/versions", ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var body = await response.Content.ReadFromJsonAsync<SecretVersionsListResponse>(JsonOptions, ct);
        body.ShouldNotBeNull();
        body!.Name.ShouldBe(ctx.Name);
        body.Scope.ShouldBe(scope);
        body.Versions.Count.ShouldBe(3);
        body.Versions[0].Version.ShouldBe(3);
        body.Versions[0].IsCurrent.ShouldBeTrue();
        body.Versions[1].Version.ShouldBe(2);
        body.Versions[1].IsCurrent.ShouldBeFalse();
        body.Versions[2].Version.ShouldBe(1);
        body.Versions[2].IsCurrent.ShouldBeFalse();
    }

    [Theory]
    [MemberData(nameof(ScopedRoutes))]
    public async Task GetVersions_MissingSecret_Returns404(SecretScope scope, string _label)
    {
        _ = _label;
        var ct = TestContext.Current.CancellationToken;
        var (basePath, _ownerId) = ResolveRoute(scope);
        _ = _ownerId;

        if (scope == SecretScope.Unit)
        {
            StubUnit(basePath.Split('/').Last().Replace("secrets", "").Trim('/'));
        }

        var response = await _client.GetAsync(
            $"{basePath}/does-not-exist-{Guid.NewGuid():N}/versions", ct);
        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        response.Content.Headers.ContentType?.MediaType.ShouldBe("application/problem+json");
    }

    [Theory]
    [MemberData(nameof(ScopedRoutes))]
    public async Task GetVersions_AccessPolicyDeniesList_Returns403(SecretScope scope, string _label)
    {
        _ = _label;
        var ct = TestContext.Current.CancellationToken;
        var ctx = await SeedAsync(scope, passThrough: true, ct);

        _factory.SecretAccessPolicy.ClearReceivedCalls();
        _factory.SecretAccessPolicy
            .IsAuthorizedAsync(SecretAccessAction.List, scope, Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(false));

        try
        {
            var response = await _client.GetAsync($"{ctx.BasePath}/{ctx.Name}/versions", ct);
            response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        }
        finally
        {
            RestoreAllowAllPolicy();
        }
    }

    // -----------------------------------------------------------------
    // POST /prune
    // -----------------------------------------------------------------

    [Theory]
    [MemberData(nameof(ScopedRoutes))]
    public async Task Prune_Keep1_RemovesOlderVersions_LeavesCurrent(SecretScope scope, string _label)
    {
        _ = _label;
        var ct = TestContext.Current.CancellationToken;
        var ctx = await SeedAsync(scope, passThrough: true, ct);

        await RotateAsync(ctx, "v2", ct);

        // Store.DeleteAsync must be invoked once — for v1's platform-
        // owned slot.
        _factory.SecretStore.ClearReceivedCalls();

        var response = await _client.PostAsync(
            $"{ctx.BasePath}/{ctx.Name}/prune?keep=1", content: null, ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var body = await response.Content.ReadFromJsonAsync<PruneSecretResponse>(JsonOptions, ct);
        body.ShouldNotBeNull();
        body!.Keep.ShouldBe(1);
        body.Pruned.ShouldBe(1);

        await _factory.SecretStore.Received(1)
            .DeleteAsync(ctx.OriginalStoreKey!, Arg.Any<CancellationToken>());

        using var serviceScope = _factory.Services.CreateScope();
        var db = serviceScope.ServiceProvider.GetRequiredService<SpringDbContext>();
        var rows = db.SecretRegistryEntries
            .Where(e => e.Scope == scope && e.OwnerId == ctx.OwnerId && e.Name == ctx.Name)
            .ToList();
        rows.Count.ShouldBe(1);
        rows[0].Version.ShouldBe(2);
    }

    [Theory]
    [MemberData(nameof(ScopedRoutes))]
    public async Task Prune_ExternalReferenceVersions_NeverCallsStoreDelete(SecretScope scope, string _label)
    {
        _ = _label;
        var ct = TestContext.Current.CancellationToken;
        var ctx = await SeedAsync(scope, passThrough: false, ct);

        // Rotate to a new external key so we have v1 + v2 both
        // external-reference.
        var rotateResponse = await _client.PutAsJsonAsync(
            $"{ctx.BasePath}/{ctx.Name}",
            new RotateSecretRequest(ExternalStoreKey: "kv://v2"),
            ct);
        rotateResponse.StatusCode.ShouldBe(HttpStatusCode.OK);

        _factory.SecretStore.ClearReceivedCalls();

        var response = await _client.PostAsync(
            $"{ctx.BasePath}/{ctx.Name}/prune?keep=1", content: null, ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var body = await response.Content.ReadFromJsonAsync<PruneSecretResponse>(JsonOptions, ct);
        body!.Pruned.ShouldBe(1);

        // External-reference versions never call the store delete —
        // the customer owns those slots.
        await _factory.SecretStore.DidNotReceive()
            .DeleteAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [MemberData(nameof(ScopedRoutes))]
    public async Task Prune_KeepGreaterOrEqualToCount_IsNoOp_Returns0(SecretScope scope, string _label)
    {
        _ = _label;
        var ct = TestContext.Current.CancellationToken;
        var ctx = await SeedAsync(scope, passThrough: true, ct);

        var response = await _client.PostAsync(
            $"{ctx.BasePath}/{ctx.Name}/prune?keep=10", content: null, ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var body = await response.Content.ReadFromJsonAsync<PruneSecretResponse>(JsonOptions, ct);
        body!.Pruned.ShouldBe(0);
    }

    [Theory]
    [MemberData(nameof(ScopedRoutes))]
    public async Task Prune_KeepZero_Returns400(SecretScope scope, string _label)
    {
        _ = _label;
        var ct = TestContext.Current.CancellationToken;
        var ctx = await SeedAsync(scope, passThrough: true, ct);

        var response = await _client.PostAsync(
            $"{ctx.BasePath}/{ctx.Name}/prune?keep=0", content: null, ct);
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Theory]
    [MemberData(nameof(ScopedRoutes))]
    public async Task Prune_MissingSecret_Returns404(SecretScope scope, string _label)
    {
        _ = _label;
        var ct = TestContext.Current.CancellationToken;
        var (basePath, _ownerId) = ResolveRoute(scope);
        _ = _ownerId;

        if (scope == SecretScope.Unit)
        {
            StubUnit(basePath.Split('/').Last().Replace("secrets", "").Trim('/'));
        }

        var response = await _client.PostAsync(
            $"{basePath}/does-not-exist-{Guid.NewGuid():N}/prune?keep=1", content: null, ct);
        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Theory]
    [MemberData(nameof(ScopedRoutes))]
    public async Task Prune_AccessPolicyDeniesPrune_Returns403(SecretScope scope, string _label)
    {
        // Pruning requires the new SecretAccessAction.Prune grant.
        _ = _label;
        var ct = TestContext.Current.CancellationToken;
        var ctx = await SeedAsync(scope, passThrough: true, ct);

        _factory.SecretAccessPolicy.ClearReceivedCalls();
        _factory.SecretAccessPolicy
            .IsAuthorizedAsync(SecretAccessAction.Prune, scope, Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(false));

        try
        {
            var response = await _client.PostAsync(
                $"{ctx.BasePath}/{ctx.Name}/prune?keep=1", content: null, ct);
            response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        }
        finally
        {
            RestoreAllowAllPolicy();
        }
    }

    // -----------------------------------------------------------------
    // test infrastructure
    // -----------------------------------------------------------------

    private record SeedContext(
        string Name,
        string BasePath,
        string OwnerId,
        string? OriginalStoreKey);

    private async Task<SeedContext> SeedAsync(SecretScope scope, bool passThrough, CancellationToken ct)
    {
        var name = $"mv-secret-{Guid.NewGuid():N}";
        var (basePath, ownerId) = ResolveRoute(scope);

        if (scope == SecretScope.Unit)
        {
            StubUnit(ownerId);
        }

        CreateSecretRequest request = passThrough
            ? new(name, Value: "original-plaintext")
            : new(name, ExternalStoreKey: "kv://v1");

        var post = await _client.PostAsJsonAsync(basePath, request, ct);
        post.StatusCode.ShouldBe(HttpStatusCode.Created);

        using var serviceScope = _factory.Services.CreateScope();
        var db = serviceScope.ServiceProvider.GetRequiredService<SpringDbContext>();
        var row = db.SecretRegistryEntries.Single(
            e => e.Scope == scope && e.OwnerId == ownerId && e.Name == name);
        return new SeedContext(name, basePath, ownerId, row.StoreKey);
    }

    private async Task RotateAsync(SeedContext ctx, string newValue, CancellationToken ct)
    {
        var response = await _client.PutAsJsonAsync(
            $"{ctx.BasePath}/{ctx.Name}",
            new RotateSecretRequest(Value: newValue),
            ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
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

    private void RestoreAllowAllPolicy()
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