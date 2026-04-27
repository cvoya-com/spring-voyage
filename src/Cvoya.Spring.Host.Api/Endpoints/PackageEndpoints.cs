// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Api.Endpoints;

using Cvoya.Spring.Host.Api.Models;
using Cvoya.Spring.Host.Api.Services;

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

/// <summary>
/// Maps package catalog endpoints. The original #316 surface exposed
/// <c>/templates</c> to feed the unit creation wizard; this PR extends
/// the surface with package-level browse endpoints (#395) that the
/// portal's <c>/packages</c> route and the <c>spring package</c> CLI
/// verb family both consume.
///
/// <list type="bullet">
///   <item><description><c>GET /api/v1/packages</c> — list installed packages with content counts.</description></item>
///   <item><description><c>GET /api/v1/packages/{name}</c> — package detail (templates + agents + skills + connectors + workflows).</description></item>
///   <item><description><c>GET /api/v1/packages/templates</c> — flat template list (kept for wizard parity).</description></item>
///   <item><description><c>GET /api/v1/packages/{package}/templates/{name}</c> — raw YAML for a single unit template.</description></item>
/// </list>
///
/// The contract is deliberately forward compatible with the Phase-6
/// install flow (#417 / PR-PLAT-PKG-2): summaries and details expose
/// stable fields only, and version + source URL can be appended later
/// without breaking the browse consumers.
/// </summary>
public static class PackageEndpoints
{
    /// <summary>
    /// Registers package-related endpoints on the supplied route builder.
    /// </summary>
    public static RouteGroupBuilder MapPackageEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/tenant/packages")
            .WithTags("Packages");

        // Use an empty route template (not "/") so the effective path
        // stays at the group prefix — `/api/v1/packages`. Routing "/" on
        // a group published at `/api/v1/packages` produces a path with a
        // trailing slash that Kiota consumers would not hit by default.
        group.MapGet(string.Empty, ListPackagesAsync)
            .WithName("ListPackages")
            .WithSummary("List installed packages with per-package content counts")
            .Produces<PackageSummary[]>(StatusCodes.Status200OK);

        group.MapGet("/templates", ListUnitTemplatesAsync)
            .WithName("ListUnitTemplates")
            .WithSummary("List unit templates discovered in the packages tree")
            .Produces<UnitTemplateSummary[]>(StatusCodes.Status200OK);

        // Order matters: the more specific /templates/{name} pattern is
        // registered before /{name} so the package-detail route never
        // swallows the template endpoint.
        group.MapGet("/{package}/templates/{name}", GetUnitTemplateAsync)
            .WithName("GetUnitTemplate")
            .WithSummary("Returns the raw YAML for a unit template inside a package")
            .Produces<UnitTemplateDetail>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status404NotFound);

        group.MapGet("/{name}", GetPackageAsync)
            .WithName("GetPackage")
            .WithSummary("Returns detailed contents of a single package")
            .Produces<PackageDetail>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status404NotFound);

        return group;
    }

    private static async Task<IResult> ListPackagesAsync(
        [FromServices] IPackageCatalogService catalog,
        CancellationToken cancellationToken)
    {
        var packages = await catalog.ListPackagesAsync(cancellationToken);
        return Results.Ok(packages);
    }

    private static async Task<IResult> GetPackageAsync(
        string name,
        [FromServices] IPackageCatalogService catalog,
        CancellationToken cancellationToken)
    {
        var detail = await catalog.GetPackageAsync(name, cancellationToken);
        return detail is null
            ? Results.NotFound()
            : Results.Ok(detail);
    }

    private static async Task<IResult> ListUnitTemplatesAsync(
        [FromServices] IPackageCatalogService catalog,
        CancellationToken cancellationToken)
    {
        var templates = await catalog.ListUnitTemplatesAsync(cancellationToken);
        return Results.Ok(templates);
    }

    private static async Task<IResult> GetUnitTemplateAsync(
        string package,
        string name,
        [FromServices] IPackageCatalogService catalog,
        CancellationToken cancellationToken)
    {
        var yaml = await catalog.LoadUnitTemplateYamlAsync(package, name, cancellationToken);
        if (yaml is null)
        {
            return Results.NotFound();
        }

        return Results.Ok(new UnitTemplateDetail(
            Package: package,
            Name: name,
            // The full repo-relative path isn't known to the endpoint
            // without a second round-trip to the catalog; the CLI and
            // portal only display the package/name pair today, so we
            // surface the conventional location rather than force an
            // extra FS call.
            Path: $"{package}/units/{name}.yaml",
            Yaml: yaml));
    }
}