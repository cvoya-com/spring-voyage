// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Api.Auth;

using System.Security.Claims;

using Cvoya.Spring.Core.Directory;
using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Dapr.Actors;
using Cvoya.Spring.Dapr.Auth;

using Microsoft.AspNetCore.Http;

/// <summary>
/// Existence-first authorisation helper for unit-scoped subresources
/// (<c>/api/v1/units/{id}/...</c>). Resolves the unit directory entry and
/// then evaluates the caller's effective <see cref="PermissionLevel"/>
/// against a minimum requirement.
///
/// <para>
/// The declarative <c>RequireAuthorization(PermissionPolicies.Unit*)</c>
/// path runs the permission gate as authorisation middleware — i.e.
/// <em>before</em> the endpoint handler, which means a request against a
/// non-existent unit responds <c>403</c> (authorisation failed closed
/// because the unit lookup returned no effective permission). That
/// pattern leaks existence and is asymmetric with the flat
/// <c>GET /units/{id}</c> and <c>GET /agents/{id}</c> responses, both of
/// which return <c>404</c> for an unknown id. Issue #1029 catalogues the
/// mismatch.
/// </para>
///
/// <para>
/// Unit-scoped subresource endpoints that used to carry a declarative
/// policy now drop it and call <see cref="AuthorizeAsync"/> from inside
/// the handler after performing any other work that is cheap enough to
/// run before authorisation. The helper preserves the three-way outcome
/// the previous middleware-based path expressed:
/// </para>
/// <list type="bullet">
///   <item><description><c>404 Not Found</c> — the unit does not exist.</description></item>
///   <item><description><c>403 Forbidden</c> — the caller is authenticated
///     but has no effective permission at or above the required level on
///     the target unit (or an ancestor, per the hierarchy rules in
///     <see cref="IPermissionService.ResolveEffectivePermissionAsync"/>).</description></item>
///   <item><description><c>DirectoryEntry</c> — authorisation succeeded;
///     the caller holds at least the requested level and the endpoint
///     handler continues.</description></item>
/// </list>
///
/// <para>
/// Authentication still runs ahead of every affected endpoint via the
/// <c>MapUnitEndpoints().RequireAuthorization()</c> /
/// <c>MapUnitPolicyEndpoints().RequireAuthorization()</c> group call in
/// <c>Program.cs</c>, so unauthenticated requests short-circuit with
/// <c>401</c> before the handler — and this helper — is ever reached.
/// </para>
/// </summary>
public static class UnitPermissionCheck
{
    /// <summary>
    /// Tagged outcome of <see cref="AuthorizeAsync"/>. Exactly one of
    /// <see cref="NotFound"/> / <see cref="Forbidden"/> / <see cref="Entry"/>
    /// is set on any given instance.
    /// </summary>
    /// <param name="NotFound">The unit was not registered in the directory.</param>
    /// <param name="Forbidden">
    /// The caller is authenticated but lacks the minimum required
    /// permission on the unit.
    /// </param>
    /// <param name="Entry">
    /// The resolved directory entry when authorisation succeeded. Null on
    /// the failure branches so the handler does not accidentally dereference
    /// a stale reference.
    /// </param>
    public readonly record struct Result(
        bool NotFound,
        bool Forbidden,
        DirectoryEntry? Entry)
    {
        /// <summary>Authorisation succeeded; safe to continue the handler.</summary>
        public bool Authorized => Entry is not null;
    }

    /// <summary>
    /// Evaluates the existence-first + permission-check pipeline for the
    /// given unit <paramref name="unitId"/>.
    /// </summary>
    /// <param name="unitId">The route-level unit id (from <c>{id}</c>).</param>
    /// <param name="minimumPermission">
    /// The minimum <see cref="PermissionLevel"/> required for the call.
    /// </param>
    /// <param name="directoryService">Resolves the unit address.</param>
    /// <param name="permissionService">Evaluates effective permission.</param>
    /// <param name="httpContext">Ambient context used to read the caller id.</param>
    /// <param name="cancellationToken">Propagation token.</param>
    /// <returns>A <see cref="Result"/> with exactly one branch set.</returns>
    public static async Task<Result> AuthorizeAsync(
        string unitId,
        PermissionLevel minimumPermission,
        IDirectoryService directoryService,
        IPermissionService permissionService,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        // Existence-first: a missing unit surfaces as 404 regardless of
        // whether the caller would have had permission on a hypothetical
        // unit with the same id. This is the fix for #1029 — the previous
        // declarative gate returned 403 here because the permission
        // evaluator saw no grant for a unit that did not exist.
        var address = Address.For("unit", unitId);
        var entry = await directoryService.ResolveAsync(address, cancellationToken);
        if (entry is null)
        {
            return new Result(NotFound: true, Forbidden: false, Entry: null);
        }

        var userId = httpContext.User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(userId))
        {
            // Defence-in-depth: authentication middleware should have
            // rejected the request with 401 already. If somehow a caller
            // with no NameIdentifier claim made it here, treat as forbidden
            // rather than succeeding the gate.
            return new Result(NotFound: false, Forbidden: true, Entry: null);
        }

        var permission = await permissionService.ResolveEffectivePermissionAsync(
            userId, unitId, cancellationToken);
        if (permission is null || (int)permission.Value < (int)minimumPermission)
        {
            return new Result(NotFound: false, Forbidden: true, Entry: null);
        }

        return new Result(NotFound: false, Forbidden: false, Entry: entry);
    }

    /// <summary>
    /// Convenience mapper: translates a failed <see cref="Result"/> into the
    /// matching <see cref="IResult"/>. Call only on non-<see cref="Result.Authorized"/>
    /// results.
    /// </summary>
    /// <param name="result">The failed authorisation result.</param>
    /// <param name="unitId">The unit id to embed in the 404 detail message.</param>
    public static IResult ToErrorResult(this Result result, string unitId)
    {
        if (result.NotFound)
        {
            return Results.Problem(
                detail: $"Unit '{unitId}' not found",
                statusCode: StatusCodes.Status404NotFound);
        }

        // Forbidden fall-through — no body is provided because the previous
        // declarative gate also returned an empty 403, and the
        // <c>ProblemDetails</c> middleware fills in a default shape. Callers
        // distinguish this from 404 by status code, which is the whole
        // point of the #1029 fix.
        return Results.Problem(statusCode: StatusCodes.Status403Forbidden);
    }
}
