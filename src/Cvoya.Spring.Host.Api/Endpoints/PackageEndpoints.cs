// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Api.Endpoints;

using Cvoya.Spring.Host.Api.Models;
using Cvoya.Spring.Host.Api.Services;

using Microsoft.AspNetCore.Mvc;

/// <summary>
/// Maps package catalog endpoints consumed by the unit creation wizard
/// (Step 2 — Template card).
/// </summary>
public static class PackageEndpoints
{
    /// <summary>
    /// Registers package-related endpoints on the supplied route builder.
    /// </summary>
    public static RouteGroupBuilder MapPackageEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/packages")
            .WithTags("Packages");

        group.MapGet("/templates", ListUnitTemplatesAsync)
            .WithName("ListUnitTemplates")
            .WithSummary("List unit templates discovered in the packages tree")
            .Produces<UnitTemplateSummary[]>(StatusCodes.Status200OK);

        return group;
    }

    private static async Task<IResult> ListUnitTemplatesAsync(
        [FromServices] IPackageCatalogService catalog,
        CancellationToken cancellationToken)
    {
        var templates = await catalog.ListUnitTemplatesAsync(cancellationToken);
        return Results.Ok(templates);
    }
}