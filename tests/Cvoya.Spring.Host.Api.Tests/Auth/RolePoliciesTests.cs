// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Api.Tests.Auth;

using System.Security.Claims;

using Cvoya.Spring.Core.Security;
using Cvoya.Spring.Host.Api.Auth;

using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;

using Shouldly;

using Xunit;

/// <summary>
/// Unit tests for <see cref="RolePolicies.AddPlatformRolePolicies"/>. Builds
/// a minimal DI container with the policies registered, then evaluates them
/// directly against synthetic principals via <see cref="IAuthorizationService"/>.
/// This deliberately bypasses the auth handlers and the full host so the
/// suite verifies the policy contract independently of any handler-side
/// claim emission (covered separately by
/// <see cref="AuthHandlerRoleClaimsTests"/>).
/// </summary>
public class RolePoliciesTests
{
    private static IAuthorizationService BuildAuthorizationService()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAuthorization(options => options.AddPlatformRolePolicies());
        return services.BuildServiceProvider().GetRequiredService<IAuthorizationService>();
    }

    private static ClaimsPrincipal PrincipalWithRoles(params string[] roles)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, "test-user"),
        };
        claims.AddRange(roles.Select(r => new Claim(ClaimTypes.Role, r)));
        var identity = new ClaimsIdentity(claims, authenticationType: "test");
        return new ClaimsPrincipal(identity);
    }

    private static ClaimsPrincipal Anonymous() => new(new ClaimsIdentity());

    [Theory]
    [InlineData(RolePolicies.PlatformOperator, PlatformRoles.PlatformOperator)]
    [InlineData(RolePolicies.TenantOperator, PlatformRoles.TenantOperator)]
    [InlineData(RolePolicies.TenantUser, PlatformRoles.TenantUser)]
    public async Task Authorize_PolicyMatchesPrincipalRole_Succeeds(string policyName, string roleClaim)
    {
        var service = BuildAuthorizationService();
        var principal = PrincipalWithRoles(roleClaim);

        var result = await service.AuthorizeAsync(principal, resource: null, policyName);

        result.Succeeded.ShouldBeTrue();
    }

    [Theory]
    [InlineData(RolePolicies.PlatformOperator, PlatformRoles.TenantUser)]
    [InlineData(RolePolicies.TenantOperator, PlatformRoles.PlatformOperator)]
    [InlineData(RolePolicies.TenantUser, PlatformRoles.TenantOperator)]
    public async Task Authorize_PrincipalMissingRole_Fails(string policyName, string unrelatedRole)
    {
        var service = BuildAuthorizationService();
        var principal = PrincipalWithRoles(unrelatedRole);

        var result = await service.AuthorizeAsync(principal, resource: null, policyName);

        result.Succeeded.ShouldBeFalse();
    }

    [Theory]
    [InlineData(RolePolicies.PlatformOperator)]
    [InlineData(RolePolicies.TenantOperator)]
    [InlineData(RolePolicies.TenantUser)]
    public async Task Authorize_AnonymousPrincipal_Fails(string policyName)
    {
        var service = BuildAuthorizationService();
        var principal = Anonymous();

        var result = await service.AuthorizeAsync(principal, resource: null, policyName);

        result.Succeeded.ShouldBeFalse();
    }

    [Fact]
    public async Task Authorize_PrincipalWithAllThreeRoles_PassesEveryPolicy()
    {
        // Mirror of the OSS overlay: a single principal carrying all three
        // role claims passes every named policy. This exercises the
        // OSS-grants-all path independent of the handler wiring.
        var service = BuildAuthorizationService();
        var principal = PrincipalWithRoles(
            PlatformRoles.PlatformOperator,
            PlatformRoles.TenantOperator,
            PlatformRoles.TenantUser);

        var policyNames = new[]
        {
            RolePolicies.PlatformOperator,
            RolePolicies.TenantOperator,
            RolePolicies.TenantUser,
        };

        foreach (var policy in policyNames)
        {
            var result = await service.AuthorizeAsync(principal, resource: null, policy);
            result.Succeeded.ShouldBeTrue($"policy '{policy}' should pass for an all-roles principal");
        }
    }
}