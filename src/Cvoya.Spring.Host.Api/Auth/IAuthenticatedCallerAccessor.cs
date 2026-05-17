// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Api.Auth;

using Cvoya.Spring.Core.Messaging;

/// <summary>
/// Resolves the identity of the caller of the current HTTP request.
/// Endpoints that dispatch messages on a user's behalf use this to thread
/// the authenticated subject's identity through <see cref="IMessageRouter"/>,
/// so the router's permission gate evaluates against the real caller rather
/// than a synthetic sender (issue #339).
/// </summary>
/// <remarks>
/// When no authenticated principal is available (out-of-request contexts,
/// anonymous endpoints, or the LocalDev/ApiToken auth handlers have not
/// surfaced a <see cref="System.Security.Claims.ClaimTypes.NameIdentifier"/>
/// claim) the accessor returns <see langword="null"/> from
/// <see cref="GetCallerAddressAsync"/>. Callers handle that explicitly —
/// authenticated endpoints fail with 401, background paths degrade.
/// Post-ADR-0036 every <see cref="Address"/> is Guid-keyed, so there is no
/// usable synthetic fallback identity at the type level (#2405).
/// </remarks>
public interface IAuthenticatedCallerAccessor
{
    /// <summary>
    /// Returns the stable Guid-keyed address representing the authenticated
    /// caller, resolving the JWT username to a UUID via
    /// <see cref="Cvoya.Spring.Core.Security.IHumanIdentityResolver"/>.
    /// Returns <see langword="null"/> when no authenticated principal is
    /// present (out-of-request paths, anonymous handlers). Callers must
    /// branch on null and either degrade or surface a 401.
    /// </summary>
    /// <param name="cancellationToken">Propagates request cancellation.</param>
    Task<Address?> GetCallerAddressAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the JWT username claim (NameIdentifier) for the current
    /// authenticated user, or the fallback value when no principal is
    /// present. Kept on the interface so legacy diagnostics that log a
    /// username string keep working; new identity comparisons go through
    /// <see cref="GetCallerAddressAsync"/> and the Guid identity it carries.
    /// </summary>
    string GetUsername();
}
