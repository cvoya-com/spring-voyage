// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Security;

/// <summary>
/// Platform-wide role names used by the API host's authorization policies.
/// </summary>
/// <remarks>
/// <para>
/// These names live in <c>Cvoya.Spring.Core</c> so both the OSS host and the
/// private cloud overlay can reference the same identifiers without
/// duplicating string constants. The OSS host's auth handlers grant every
/// authenticated caller all three roles by default (single-user OSS
/// deployments). The cloud overlay swaps in its own role-claim source that
/// scopes the granted subset per identity.
/// </para>
/// <para>
/// Endpoint-by-endpoint role gating is deliberately out of scope for the
/// declaration of these names — sub-issue C1.2b applies <c>RolePolicies</c>
/// to the existing endpoint surface, splits platform/tenant URL groups, and
/// adjusts tests. This class is the keystone the rest of Area C builds on.
/// </para>
/// </remarks>
public static class PlatformRoles
{
    /// <summary>
    /// Operates the SV platform itself: tenant CRUD, system credentials,
    /// platform secrets, runtime registration. The cloud overlay grants this
    /// role only to platform-staff identities.
    /// </summary>
    public const string PlatformOperator = "PlatformOperator";

    /// <summary>
    /// Configures a tenant: agent-runtime + connector installs, tenant
    /// secrets, GitHub App, BYOI, cloning policy, budget. The cloud overlay
    /// grants this role to tenant administrators.
    /// </summary>
    public const string TenantOperator = "TenantOperator";

    /// <summary>
    /// Uses Spring Voyage inside a tenant: messaging, observing, units /
    /// agents, dashboard, conversations. The cloud overlay grants this role
    /// to ordinary tenant users.
    /// </summary>
    public const string TenantUser = "TenantUser";

    /// <summary>
    /// Read-only enumeration of every platform role name. Useful for OSS
    /// overlays that need to grant all three claims and for tests that assert
    /// the full set.
    /// </summary>
    public static IReadOnlyList<string> All { get; } = new[]
    {
        PlatformOperator,
        TenantOperator,
        TenantUser,
    };
}