// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Api.Auth;

using Cvoya.Spring.Core.Security;

using Microsoft.AspNetCore.Authorization;

/// <summary>
/// Named authorization policies that gate endpoints on platform-role claims.
/// </summary>
/// <remarks>
/// <para>
/// Each policy requires authentication AND that the caller carry the
/// matching role claim emitted by <see cref="IRoleClaimSource"/>. The OSS
/// overlay grants all three roles to every authenticated caller, so each
/// policy passes uniformly there; the cloud overlay scopes the granted
/// subset per identity, and policies deny when the role is absent.
/// </para>
/// <para>
/// Endpoint-by-endpoint application of these policy names is C1.2b — this
/// file only declares the policies. The names below are the contract every
/// downstream <c>RequireAuthorization(RolePolicies.X)</c> call site
/// targets.
/// </para>
/// </remarks>
public static class RolePolicies
{
    /// <summary>
    /// Policy name requiring the <see cref="PlatformRoles.PlatformOperator"/>
    /// role. Reserved for endpoints that mutate platform-wide state.
    /// </summary>
    public const string PlatformOperator = PlatformRoles.PlatformOperator;

    /// <summary>
    /// Policy name requiring the <see cref="PlatformRoles.TenantOperator"/>
    /// role. Reserved for endpoints that mutate a tenant's configuration.
    /// </summary>
    public const string TenantOperator = PlatformRoles.TenantOperator;

    /// <summary>
    /// Policy name requiring the <see cref="PlatformRoles.TenantUser"/>
    /// role. Reserved for in-product usage endpoints.
    /// </summary>
    public const string TenantUser = PlatformRoles.TenantUser;

    /// <summary>
    /// Registers the platform-role policies on <paramref name="options"/>.
    /// Mirrors the shape of <see cref="PermissionPolicies.AddUnitPermissionPolicies"/>.
    /// </summary>
    /// <param name="options">The authorization options to configure.</param>
    public static void AddPlatformRolePolicies(this AuthorizationOptions options)
    {
        options.AddPolicy(PlatformOperator, policy =>
            policy
                .RequireAuthenticatedUser()
                .RequireRole(PlatformRoles.PlatformOperator));

        options.AddPolicy(TenantOperator, policy =>
            policy
                .RequireAuthenticatedUser()
                .RequireRole(PlatformRoles.TenantOperator));

        options.AddPolicy(TenantUser, policy =>
            policy
                .RequireAuthenticatedUser()
                .RequireRole(PlatformRoles.TenantUser));
    }
}