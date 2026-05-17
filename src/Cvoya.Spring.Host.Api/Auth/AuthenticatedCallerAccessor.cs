// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Api.Auth;

using System.Security.Claims;

using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Core.Security;

using Microsoft.AspNetCore.Http;

/// <summary>
/// Default <see cref="IAuthenticatedCallerAccessor"/> implementation. Reads
/// the <see cref="ClaimTypes.NameIdentifier"/> claim from the ambient
/// <see cref="HttpContext"/> to derive the caller's stable UUID, then emits
/// the Guid-keyed address <c>human:&lt;uuid&gt;</c> via
/// <see cref="IHumanIdentityResolver"/>. When no authenticated principal is
/// present (out-of-request contexts, anonymous handlers) the accessor
/// returns <see langword="null"/> — post-ADR-0036 there is no usable
/// non-Guid fallback identity, and callers branch on null explicitly
/// (#2405).
/// </summary>
public sealed class AuthenticatedCallerAccessor(
    IHttpContextAccessor httpContextAccessor,
    IHumanIdentityResolver identityResolver) : IAuthenticatedCallerAccessor
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
    public async Task<Address?> GetCallerAddressAsync(CancellationToken cancellationToken = default)
    {
        var user = httpContextAccessor.HttpContext?.User;
        if (user?.Identity?.IsAuthenticated == true)
        {
            var claim = user.FindFirstValue(ClaimTypes.NameIdentifier);
            if (!string.IsNullOrWhiteSpace(claim))
            {
                var displayName = user.FindFirstValue(ClaimTypes.Name);
                var id = await identityResolver.ResolveByUsernameAsync(claim, displayName, cancellationToken);
                return Address.ForIdentity("human", id);
            }
        }

        // No authenticated principal: return null so callers can branch
        // explicitly. Pre-#2405 we returned Address.For("human", "api"),
        // which throws InvalidAddressIdException post-ADR-0036 because
        // "api" is not a Guid — the fallback path was already dead.
        return null;
    }
}
