// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Api.Endpoints;

/// <summary>
/// ADR-0047 §14: the connector-identity routes relocate under
/// <c>/api/v1/tenant/users/{tenantUserId}/identities</c>. The retired
/// route stub returns 410 Gone with a structured migration hint pointing
/// at the new path. Mirrors the pattern <see cref="LegacyOrchestrationEndpoints"/>
/// uses for ADR-0039 §E9 retirement of the unit orchestration endpoint.
/// </summary>
/// <remarks>
/// <para>
/// Only the <c>/identities</c> sub-routes retire — the per-<c>Human</c>
/// envelope routes (<c>GET</c> / <c>PATCH /api/v1/tenant/humans/{id}</c>)
/// stay where they are because they remain unit-membership concerns
/// owned by ADR-0046, not the connector-identity surface owned by
/// ADR-0047.
/// </para>
/// <para>
/// The CLI's <c>spring human identity …</c> verbs will surface this 410
/// at the API layer until Phase G renames them to
/// <c>spring user identity …</c>. The structured body's <c>code</c>
/// extension lets the CLI's error handler print a clean "the route
/// moved; use <c>spring user identity</c> instead" hint without parsing
/// free-form text.
/// </para>
/// </remarks>
internal static class RetiredHumanIdentityEndpoints
{
    /// <summary>
    /// Stable URI placed into <c>type</c> on the migration-hint response.
    /// </summary>
    private const string ProblemType =
        "https://docs.cvoya.com/spring/errors/route-retired-adr-0047";

    /// <summary>
    /// Structured <c>code</c> extension value identifying the retired
    /// surface. The CLI / portal pattern-match on this code rather than
    /// parsing the title.
    /// </summary>
    private const string ErrorCode = "HumanIdentityEndpointsRetired";

    private static readonly string[] Methods =
    [
        HttpMethods.Get,
        HttpMethods.Post,
        HttpMethods.Put,
        HttpMethods.Delete,
        HttpMethods.Patch,
    ];

    /// <summary>
    /// Registers the 410 stub at the old
    /// <c>/api/v1/tenant/humans/{humanId}/identities</c> path. All HTTP
    /// verbs surface the same migration-hint body.
    /// </summary>
    internal static IEndpointRouteBuilder MapRetiredHumanIdentityEndpoints(
        this IEndpointRouteBuilder app)
    {
        app.MapMethods(
                "/api/v1/tenant/humans/{humanId}/identities",
                Methods,
                (string humanId) => Retired(humanId))
            .WithTags("Legacy")
            .ExcludeFromDescription();

        return app;
    }

    private static IResult Retired(string humanId)
    {
        _ = humanId;

        return Results.Problem(
            type: ProblemType,
            title: "Connector-identity endpoints have moved",
            detail: "The /api/v1/tenant/humans/{humanId}/identities routes are " +
                    "retired in ADR-0047 §14. Connector identities now live on the " +
                    "TenantUser principal, not the Human row. Use " +
                    "/api/v1/tenant/users/{tenantUserId}/identities instead. The " +
                    "per-Human envelope routes (GET / PATCH /api/v1/tenant/humans/{id}) " +
                    "are unchanged.",
            statusCode: StatusCodes.Status410Gone,
            extensions: new Dictionary<string, object?>
            {
                ["code"] = ErrorCode,
                ["newRoute"] = "/api/v1/tenant/users/{tenantUserId}/identities",
            });
    }
}
