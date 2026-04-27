// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Api.Auth;

using System.Security.Claims;

/// <summary>
/// Resolves the platform-role claims (see
/// <see cref="Cvoya.Spring.Core.Security.PlatformRoles"/>) to attach to an
/// authenticated principal. Both <see cref="ApiTokenAuthHandler"/> and
/// <see cref="LocalDevAuthHandler"/> consult this source when constructing
/// the <see cref="ClaimsIdentity"/> for a successfully authenticated caller.
/// </summary>
/// <remarks>
/// <para>
/// The OSS host registers <see cref="OssAllRolesClaimSource"/> as the
/// default, which grants every authenticated caller all three platform
/// roles — that matches single-user OSS deployments where the operator and
/// the user are the same person.
/// </para>
/// <para>
/// The cloud overlay (private repo) registers its own
/// <see cref="IRoleClaimSource"/> via <c>TryAddSingleton</c> ahead of the
/// OSS extension call so OSS does not displace it. The cloud
/// implementation inspects the platform-issued identity (already attached
/// to the <see cref="ClaimsIdentity"/> by the cloud auth pipeline) and
/// returns only the roles that apply per directory lookup.
/// </para>
/// </remarks>
public interface IRoleClaimSource
{
    /// <summary>
    /// Returns the role claims to add to <paramref name="identity"/>.
    /// </summary>
    /// <param name="identity">
    /// The just-authenticated identity. Implementations MAY inspect existing
    /// claims (e.g. <see cref="ClaimTypes.NameIdentifier"/>) to scope the
    /// result; the OSS implementation ignores it and returns all three roles
    /// unconditionally.
    /// </param>
    /// <returns>
    /// One <see cref="Claim"/> per role to grant. The auth handler appends
    /// these to the identity before producing the
    /// <see cref="System.Security.Claims.ClaimsPrincipal"/>. The collection
    /// MAY be empty (cloud overlay's "no roles for this caller") — the
    /// downstream policies will then deny.
    /// </returns>
    IEnumerable<Claim> GetRoleClaims(ClaimsIdentity identity);
}