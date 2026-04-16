// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Api.Auth;

using System.Security.Claims;

using Cvoya.Spring.Core.Messaging;

using Microsoft.AspNetCore.Http;

/// <summary>
/// Default <see cref="IAuthenticatedCallerAccessor"/> implementation. Reads
/// the <see cref="ClaimTypes.NameIdentifier"/> claim from the ambient
/// <see cref="HttpContext"/> to derive the caller's <c>human://</c> address.
/// </summary>
/// <remarks>
/// Mirrors the fallback pattern <c>UnitCreationService</c> uses for
/// resolving the creator identity (#328): the claim is preferred whenever
/// an authenticated principal is present, otherwise the synthetic
/// <c>human://api</c> identity is returned so platform-internal call sites
/// (e.g. background work outside a request) keep working.
/// </remarks>
public sealed class AuthenticatedCallerAccessor(
    IHttpContextAccessor httpContextAccessor) : IAuthenticatedCallerAccessor
{
    /// <summary>
    /// Path used on the synthetic <c>human://</c> address when no
    /// authenticated subject is available. Matches
    /// <c>UnitCreationService.FallbackCreatorId</c> so the same identity
    /// threads through every platform-internal code path.
    /// </summary>
    public const string FallbackHumanId = "api";

    /// <inheritdoc />
    public Address GetHumanAddress()
    {
        var user = httpContextAccessor.HttpContext?.User;
        if (user?.Identity?.IsAuthenticated == true)
        {
            var claim = user.FindFirstValue(ClaimTypes.NameIdentifier);
            if (!string.IsNullOrWhiteSpace(claim))
            {
                return new Address("human", claim);
            }
        }

        return new Address("human", FallbackHumanId);
    }
}