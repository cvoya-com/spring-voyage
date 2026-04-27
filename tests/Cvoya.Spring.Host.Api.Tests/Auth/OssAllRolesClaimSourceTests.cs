// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Api.Tests.Auth;

using System.Security.Claims;

using Cvoya.Spring.Core.Security;
using Cvoya.Spring.Host.Api.Auth;

using Shouldly;

using Xunit;

/// <summary>
/// Unit tests for <see cref="OssAllRolesClaimSource"/> verifying the
/// OSS-grants-all contract (C1.2a / #1257).
/// </summary>
public class OssAllRolesClaimSourceTests
{
    [Fact]
    public void GetRoleClaims_AnyIdentity_ReturnsAllThreePlatformRoles()
    {
        var sut = new OssAllRolesClaimSource();
        var identity = new ClaimsIdentity(
            new[] { new Claim(ClaimTypes.NameIdentifier, "alice") },
            authenticationType: "test");

        var claims = sut.GetRoleClaims(identity).ToList();

        claims.Count.ShouldBe(3);
        claims.ShouldAllBe(c => c.Type == ClaimTypes.Role);
        claims.Select(c => c.Value).ShouldBe(
            new[]
            {
                PlatformRoles.PlatformOperator,
                PlatformRoles.TenantOperator,
                PlatformRoles.TenantUser,
            },
            ignoreOrder: true);
    }

    [Fact]
    public void GetRoleClaims_AnonymousIdentity_StillReturnsAllThree()
    {
        // OSS deployments emit all three roles unconditionally — even an
        // identity without a NameIdentifier claim still gets the full set.
        // Cloud overlays scope per identity in their own implementation.
        var sut = new OssAllRolesClaimSource();
        var identity = new ClaimsIdentity();

        var claims = sut.GetRoleClaims(identity).ToList();

        claims.Count.ShouldBe(3);
    }

    [Fact]
    public void GetRoleClaims_DoesNotMutateIdentity()
    {
        // The contract is "return claims to add", not "modify the identity"
        // — the auth handler does the AddClaims call. Defensive check that
        // future refactors don't accidentally introduce side-effects.
        var sut = new OssAllRolesClaimSource();
        var identity = new ClaimsIdentity(
            new[] { new Claim(ClaimTypes.NameIdentifier, "alice") },
            authenticationType: "test");
        var beforeCount = identity.Claims.Count();

        _ = sut.GetRoleClaims(identity).ToList();

        identity.Claims.Count().ShouldBe(beforeCount);
    }

    [Fact]
    public void PlatformRoles_All_MatchesNamedConstants()
    {
        // Catches accidental drift between the All collection and the
        // individual constants that policies and tests reference.
        PlatformRoles.All.ShouldBe(
            new[]
            {
                PlatformRoles.PlatformOperator,
                PlatformRoles.TenantOperator,
                PlatformRoles.TenantUser,
            });
    }
}