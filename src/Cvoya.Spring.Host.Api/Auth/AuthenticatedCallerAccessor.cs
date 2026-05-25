// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Api.Auth;

using System.Security.Claims;

using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Core.Tenancy;

using Microsoft.AspNetCore.Http;

/// <summary>
/// Default <see cref="IAuthenticatedCallerAccessor"/> implementation. Per
/// ADR-0047 §1 and #2768 the caller's identity in Spring Voyage is a
/// <c>TenantUser</c> — an authenticated principal scoped to one tenant —
/// not a <c>Human</c> (which models a unit team-member slot declared by a
/// package). The accessor returns the canonical
/// <c>tenant-user://&lt;OssTenantUserIds.Operator&gt;</c> address when the
/// ambient <see cref="HttpContext"/> carries an authenticated principal in
/// the OSS deployment.
/// </summary>
/// <remarks>
/// <para>
/// Pre-#2768 this accessor side-effect-upserted a <see cref="Cvoya.Spring.Dapr.Data.Entities.HumanEntity"/>
/// keyed on the JWT username on every authenticated request, then returned
/// the synthetic <c>human://&lt;auto-minted-id&gt;</c> address as the
/// caller identity. That conflated the caller with a package-declared
/// team-member slot and left two HumanEntity rows on a fresh OSS install
/// (the declared one + the auto-minted one) — the cause of the inbox bug
/// #2766. Returning the OSS operator <c>tenant-user://</c> address
/// directly removes the side effect and aligns the runtime model with
/// ADR-0047 §1.
/// </para>
/// <para>
/// When no authenticated principal is present (out-of-request contexts,
/// anonymous handlers) the accessor returns <see langword="null"/> —
/// post-ADR-0036 there is no usable non-Guid fallback identity and
/// callers branch on null explicitly (#2405).
/// </para>
/// <para>
/// The cloud overlay registers a tenant-aware variant via the
/// <c>TryAdd*</c> seam on <see cref="IAuthenticatedCallerAccessor"/>:
/// that implementation resolves <c>(tenant_id, auth_subject)</c> →
/// existing/new <see cref="Cvoya.Spring.Dapr.Data.Entities.TenantUserEntity"/>
/// row per ADR-0047 § 7, and returns the resulting
/// <c>tenant-user://&lt;tenant-user-guid&gt;</c> address. This default
/// stays the OSS single-operator shortcut.
/// </para>
/// </remarks>
public sealed class AuthenticatedCallerAccessor(
    IHttpContextAccessor httpContextAccessor) : IAuthenticatedCallerAccessor
{
    /// <summary>
    /// Username surfaced by <see cref="GetUsername"/> when no authenticated
    /// subject is available. Matches
    /// <c>UnitCreationService.FallbackCreatorId</c> so the same diagnostic
    /// label threads through every platform-internal code path that still
    /// works in string-username terms. Unrelated to address construction;
    /// the address path now returns <see langword="null"/> in the same
    /// situation (see <see cref="GetCallerAddressAsync"/>).
    /// </summary>
    public const string FallbackHumanUsername = "api";

    /// <inheritdoc />
    public string GetUsername()
    {
        var user = httpContextAccessor.HttpContext?.User;
        if (user?.Identity?.IsAuthenticated == true)
        {
            var claim = user.FindFirstValue(ClaimTypes.NameIdentifier);
            if (!string.IsNullOrWhiteSpace(claim))
            {
                return claim;
            }
        }

        return FallbackHumanUsername;
    }

    /// <inheritdoc />
    public Task<Address?> GetCallerAddressAsync(CancellationToken cancellationToken = default)
    {
        var user = httpContextAccessor.HttpContext?.User;
        if (user?.Identity?.IsAuthenticated == true)
        {
            // Defence-in-depth: require a NameIdentifier claim before
            // accepting the principal. The OSS LocalDev auth handler always
            // stamps one; a misconfigured upstream that authenticated without
            // surfacing a subject should NOT silently grant the operator's
            // identity.
            var claim = user.FindFirstValue(ClaimTypes.NameIdentifier);
            if (!string.IsNullOrWhiteSpace(claim))
            {
                // #2768: in the OSS deployment every authenticated request
                // resolves to the single operator TenantUser pinned at
                // OssTenantUserIds.Operator. The DefaultTenantUserSeedProvider
                // materialises the row at host start, so there's no
                // upsert/auto-mint here — the address returned is the seeded
                // principal directly.
                return Task.FromResult<Address?>(
                    Address.ForIdentity(Address.TenantUserScheme, OssTenantUserIds.Operator));
            }
        }

        // No authenticated principal: return null so callers can branch
        // explicitly. Pre-#2405 we returned Address.For("human", "api"),
        // which throws InvalidAddressIdException post-ADR-0036 because
        // "api" is not a Guid — the fallback path was already dead.
        return Task.FromResult<Address?>(null);
    }
}
