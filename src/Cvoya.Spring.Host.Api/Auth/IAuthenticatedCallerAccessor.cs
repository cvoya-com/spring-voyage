// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Api.Auth;

using Cvoya.Spring.Core.Messaging;

/// <summary>
/// Resolves the <c>human://</c> <see cref="Address"/> that represents the
/// caller of the current HTTP request. Endpoints that dispatch messages on a
/// user's behalf use this to thread the authenticated subject's identity
/// through <see cref="IMessageRouter"/>, so the router's permission gate
/// evaluates against the real caller rather than a synthetic
/// <c>human://api</c> sender (issue #339).
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
    /// Returns the <c>human://</c> address representing the authenticated
    /// caller on the ambient <see cref="Microsoft.AspNetCore.Http.HttpContext"/>,
    /// or <c>human://api</c> when no authenticated subject is present.
    /// </summary>
    Address GetHumanAddress();
}