// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Api.Auth;

using System.Security.Claims;

using Cvoya.Spring.Core.Security;

/// <summary>
/// OSS-default <see cref="IRoleClaimSource"/>. Grants every authenticated
/// caller all three platform roles (<see cref="PlatformRoles.PlatformOperator"/>,
/// <see cref="PlatformRoles.TenantOperator"/>, <see cref="PlatformRoles.TenantUser"/>).
/// </summary>
/// <remarks>
/// Single-user OSS deployments treat the platform operator, tenant operator,
/// and tenant user as the same human, so the OSS overlay deliberately does
/// not gate behaviour on role boundaries — every authenticated caller can
/// do everything. The cloud overlay supplies its own
/// <see cref="IRoleClaimSource"/> via <c>TryAddSingleton</c> ahead of the
/// OSS extension call to scope the granted subset per identity.
/// </remarks>
public sealed class OssAllRolesClaimSource : IRoleClaimSource
{
    /// <inheritdoc />
    public IEnumerable<Claim> GetRoleClaims(ClaimsIdentity identity)
    {
        // ClaimTypes.Role is what [Authorize(Roles = "...")] and
        // RequireRole(...) both consult; the named-role policies wired in
        // RolePolicies use the same claim type via RequireRole. Emitting all
        // three is intentional for OSS — see class-level remarks.
        foreach (var role in PlatformRoles.All)
        {
            yield return new Claim(ClaimTypes.Role, role);
        }
    }
}