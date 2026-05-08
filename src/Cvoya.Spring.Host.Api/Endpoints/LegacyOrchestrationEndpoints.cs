// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Api.Endpoints;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

/// <summary>
/// ADR-0039 E9: the unit orchestration endpoint is removed. Return 410 Gone
/// with a migration hint for callers still using the old surface.
/// </summary>
internal static class LegacyOrchestrationEndpoints
{
    private static readonly string[] Methods =
    [
        HttpMethods.Get,
        HttpMethods.Post,
        HttpMethods.Put,
        HttpMethods.Delete,
    ];

    internal static IEndpointRouteBuilder MapLegacyOrchestrationEndpoints(
        this IEndpointRouteBuilder app)
    {
        MapGone(app, "/api/v1/tenant/units/{id}/orchestration");
        MapGone(app, "/api/v1/units/{id}/orchestration");

        return app;
    }

    private static void MapGone(IEndpointRouteBuilder app, string pattern)
    {
        app.MapMethods(pattern, Methods, (string id) => Removed(id))
            .WithTags("Legacy")
            .ExcludeFromDescription();
    }

    private static IResult Removed(string id)
    {
        _ = id;

        return Results.Problem(
            title: "Orchestration endpoint removed",
            detail: "The orchestration endpoint is removed in ADR-0039. Configure the unit's runtime instead (see docs/concepts/agents.md).",
            statusCode: StatusCodes.Status410Gone);
    }
}