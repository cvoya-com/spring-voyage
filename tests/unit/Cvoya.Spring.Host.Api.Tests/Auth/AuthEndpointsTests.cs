// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Api.Tests.Auth;

using System.Net;
using System.Net.Http.Json;

using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Core.Tenancy;
using Cvoya.Spring.Dapr.Data;
using Cvoya.Spring.Host.Api.Auth;
using Cvoya.Spring.Host.Api.Models;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using Shouldly;

using Xunit;

/// <summary>
/// Integration tests for the auth token management endpoints, plus the
/// <c>GET /auth/me</c> identity surface (#2900). The companion contract test
/// (<c>AuthContractTests.GetCurrentUser_MatchesContract</c>) pins only the
/// wire <em>shape</em>; the <c>GetCurrentUser_*</c> tests here pin the
/// <em>values</em> and the <c>Id</c>↔<c>TenantUserId</c> relationship the
/// portal reconciles against — the locus of the #2888 "operator shown
/// read-only on their own thread" defect.
/// </summary>
public class AuthEndpointsTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public AuthEndpointsTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task CreateToken_ReturnsTokenValue()
    {
        var ct = TestContext.Current.CancellationToken;
        var request = new CreateTokenRequest("my-token");

        var response = await _client.PostAsJsonAsync("/api/v1/tenant/auth/tokens", request, ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Created);

        var result = await response.Content.ReadFromJsonAsync<CreateTokenResponse>(ct);
        result.ShouldNotBeNull();
        result!.Name.ShouldBe("my-token");
        result.Token.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task ListTokens_ReturnsMetadataWithoutTokenValues()
    {
        var ct = TestContext.Current.CancellationToken;

        // Create a token first.
        var createRequest = new CreateTokenRequest("list-test-token", ["read", "write"]);
        var createResponse = await _client.PostAsJsonAsync("/api/v1/tenant/auth/tokens", createRequest, ct);
        createResponse.StatusCode.ShouldBe(HttpStatusCode.Created);

        var response = await _client.GetAsync("/api/v1/tenant/auth/tokens", ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var tokens = await response.Content.ReadFromJsonAsync<List<TokenResponse>>(ct);
        tokens.ShouldNotBeNull();
        tokens.ShouldContain(t => t.Name == "list-test-token");

        var token = tokens!.First(t => t.Name == "list-test-token");
        token.Scopes.ShouldBe(new[] { "read", "write" }, ignoreOrder: true);
    }

    [Fact]
    public async Task RevokeToken_MarksAsRevoked()
    {
        var ct = TestContext.Current.CancellationToken;

        // Create a token.
        var createRequest = new CreateTokenRequest("revoke-test-token");
        var createResponse = await _client.PostAsJsonAsync("/api/v1/tenant/auth/tokens", createRequest, ct);
        createResponse.StatusCode.ShouldBe(HttpStatusCode.Created);

        // Revoke it.
        var revokeResponse = await _client.DeleteAsync("/api/v1/tenant/auth/tokens/revoke-test-token", ct);
        revokeResponse.StatusCode.ShouldBe(HttpStatusCode.NoContent);

        // Verify it no longer appears in the list.
        var listResponse = await _client.GetAsync("/api/v1/tenant/auth/tokens", ct);
        var tokens = await listResponse.Content.ReadFromJsonAsync<List<TokenResponse>>(ct);
        tokens.ShouldNotBeNull();
        tokens.ShouldNotContain(t => t.Name == "revoke-test-token");
    }

    [Fact]
    public async Task RevokeToken_NotFound_Returns404()
    {
        var ct = TestContext.Current.CancellationToken;

        var response = await _client.DeleteAsync("/api/v1/tenant/auth/tokens/nonexistent-token", ct);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task GetCurrentUser_SurfacesAuthHatId_AndOperatorTenantUserJoinKey()
    {
        // #2900 / #2888: /me must surface the *auth-username Hat* as `Id`
        // (the canonical "is this me?" primitive — ParticipantRef.Id) and
        // the operator's stable TenantUser id as `TenantUserId` (the join
        // key the portal uses to reconcile a thread participant wearing a
        // *different* Hat owned by the same operator). The contract test
        // proves only the wire shape; the defect lived in *which* identity
        // /me returns, so assert the values and the Id↔TenantUserId
        // relationship directly.
        var ct = TestContext.Current.CancellationToken;

        var response = await _client.GetAsync("/api/v1/tenant/auth/me", ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var me = await response.Content.ReadFromJsonAsync<UserProfileResponse>(ct);
        me.ShouldNotBeNull();

        // TenantUserId is the operator join key.
        me!.TenantUserId.ShouldBe(OssTenantUserIds.Operator);

        // `Id` is a Hat (human) identity — a DISTINCT primitive from the
        // TenantUser id. Surfacing the TenantUser id as `Id` (conflating the
        // two) is the #2888-class regression; guard it explicitly.
        me.Id.ShouldNotBe(Guid.Empty);
        me.Id.ShouldNotBe(OssTenantUserIds.Operator);

        // `Id` is precisely the Hat the resolver minted/looked-up for the
        // LocalDev auth subject, and that Hat is bound to the operator
        // TenantUser (so the join key above is consistent with the FK).
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SpringDbContext>();
        var authHat = await db.Humans
            .AsNoTracking()
            .SingleAsync(h => h.Username == AuthConstants.DefaultLocalUserId, ct);
        me.Id.ShouldBe(authHat.Id);
        authHat.TenantUserId.ShouldBe(OssTenantUserIds.Operator);

        // `Address` is the wire form of that same Hat — display/routing
        // only, never identity comparison (#2082).
        me.Address.ShouldBe(Address.ForIdentity(Address.HumanScheme, authHat.Id).ToString());
    }

    [Fact]
    public async Task GetCurrentUser_MultiHat_AuthHatDiffersFromOperatorsOtherHat_SharedJoinKey()
    {
        // The multi-Hat case the single-operator fixture could never model
        // (#2900 blocker): one operator wears more than one Hat. /me returns
        // the auth-username Hat; a *different* Hat owned by the same operator
        // is a thread participant. The portal must NOT treat the operator as
        // a stranger on that thread — it reconciles via the shared TenantUser
        // join key (or the full bound-Hat set). This asserts the two Hats are
        // distinct identities yet share `TenantUserId`, which is exactly what
        // makes that reconciliation possible — the property #2888 violated.
        var ct = TestContext.Current.CancellationToken;

        var response = await _client.GetAsync("/api/v1/tenant/auth/me", ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var me = await response.Content.ReadFromJsonAsync<UserProfileResponse>(ct);
        me.ShouldNotBeNull();

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SpringDbContext>();
        var participantHat = await db.Humans
            .AsNoTracking()
            .SingleAsync(
                h => h.Id == CustomWebApplicationFactory.OperatorThreadParticipantHumanId, ct);

        // Same operator — the reconciliation key matches on both sides, so
        // the portal can recognise the participant Hat as belonging to "me".
        me!.TenantUserId.ShouldBe(OssTenantUserIds.Operator);
        participantHat.TenantUserId.ShouldBe(OssTenantUserIds.Operator);

        // … but it is a DIFFERENT Hat than the one /me surfaces. A naive
        // `me.Id == participant.Id` identity check (the #2888 bug) would
        // wrongly conclude "not me" and lock the operator out of their own
        // thread.
        participantHat.Id.ShouldNotBe(me.Id);
    }

}
