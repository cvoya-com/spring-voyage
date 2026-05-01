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
/// <see cref="HttpContext"/> to derive the caller's stable UUID, then emits the
/// identity-form address <c>human:id:&lt;uuid&gt;</c> via
/// <see cref="IHumanIdentityResolver"/>. Navigation-form fallback
/// (<c>human://api</c>) is preserved when no authenticated principal is present.
/// </summary>
public sealed class AuthenticatedCallerAccessor(
    IHttpContextAccessor httpContextAccessor,
    IHumanIdentityResolver identityResolver) : IAuthenticatedCallerAccessor
{
    /// <summary>
    /// Username used on the navigation-form fallback address when no
    /// authenticated subject is available. Matches
    /// <c>UnitCreationService.FallbackCreatorId</c> so the same identity
    /// threads through every platform-internal code path.
    /// </summary>
    public const string FallbackHumanUsername = "api";

    /// <summary>
    /// Kept for callers that reference the old constant name.
    /// </summary>
    public const string FallbackHumanId = FallbackHumanUsername;

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
    public async Task<Address> GetCallerAddressAsync(CancellationToken cancellationToken = default)
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

        // Unauthenticated / fallback: navigation form so existing
        // platform-internal call sites (background work, tests that
        // pre-date the resolver) keep working.
        return new Address("human", FallbackHumanUsername);
    }
}