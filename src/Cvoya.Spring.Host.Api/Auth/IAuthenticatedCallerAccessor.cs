// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Api.Auth;

using Cvoya.Spring.Core.Messaging;

/// <summary>
/// Resolves the identity of the caller of the current HTTP request.
/// Endpoints that dispatch messages on a user's behalf use this to thread
/// the authenticated subject's identity through <see cref="IMessageRouter"/>,
/// so the router's permission gate evaluates against the real caller rather
/// than a synthetic <c>human://api</c> sender (issue #339).
/// </summary>
/// <remarks>
/// When no authenticated principal is available (out-of-request contexts,
/// anonymous endpoints, or the LocalDev/ApiToken auth handlers have not
/// surfaced a <see cref="System.Security.Claims.ClaimTypes.NameIdentifier"/>
/// claim) the accessor falls back to the synthetic <c>human://api</c>
/// identity so existing platform-internal call sites keep working. Callers
/// that specifically want platform-internal semantics should bypass
/// <see cref="IMessageRouter"/> entirely and dispatch to the actor proxy
/// directly — the accessor is only for user-on-behalf-of dispatch.
/// </remarks>
public interface IAuthenticatedCallerAccessor
{
    /// <summary>
    /// Returns the stable identity-form address (<c>human:id:&lt;uuid&gt;</c>)
    /// representing the authenticated caller, resolving the JWT username to
    /// a UUID via <see cref="Cvoya.Spring.Core.Security.IHumanIdentityResolver"/>.
    /// Falls back to the navigation-form <c>human://api</c> address when no
    /// authenticated subject is present.
    /// </summary>
    /// <param name="cancellationToken">Propagates request cancellation.</param>
    Task<Address> GetCallerAddressAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the JWT username claim (NameIdentifier) for the current
    /// authenticated user, or the fallback value when no principal is present.
    /// </summary>
    string GetUsername();
}