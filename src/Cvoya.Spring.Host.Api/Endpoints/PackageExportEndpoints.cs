// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Api.Endpoints;

using Cvoya.Spring.Host.Api.Models;
using Cvoya.Spring.Host.Api.Services;

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

/// <summary>
/// Maps the package export endpoint (ADR-0035 decisions 9 and 12).
///
/// <list type="bullet">
///   <item>
///     <description>
///       <c>POST /api/v1/packages/export</c> — export an installed package
///       back to its original <c>package.yaml</c> manifest. Input is either a
///       unit name (resolved via the tenant directory) or a direct install id.
///       Output is the raw YAML blob with optional input-value materialisation.
///     </description>
///   </item>
/// </list>
/// </summary>
public static class PackageExportEndpoints
{
    /// <summary>
    /// Registers package-export endpoints on the supplied route builder.
    /// </summary>
    public static RouteGroupBuilder MapPackageExportEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/tenant/packages")
            .WithTags("Packages");

        group.MapPost("/export", ExportPackageAsync)
            .WithName("ExportPackage")
            .WithSummary(
                "Export an installed package back to its original package.yaml manifest. " +
                "Supply either unitName or installId (not both). " +
                "Use withValues=true to materialise resolved input values; " +
                "secret inputs are emitted as placeholder references, never as cleartext.")
            .Accepts<PackageExportRequest>("application/json")
            .Produces(StatusCodes.Status200OK, contentType: "application/x-yaml")
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound);

        return group;
    }

    private static async Task<IResult> ExportPackageAsync(
        [FromBody] PackageExportRequest request,
        [FromServices] IPackageExportService exportService,
        CancellationToken cancellationToken)
    {
        if (request is null)
        {
            return Results.Problem(
                detail: "Request body is required.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        // Validate: exactly one of unitName / installId must be provided.
        var hasUnitName = !string.IsNullOrWhiteSpace(request.UnitName);
        var hasInstallId = request.InstallId.HasValue;

        if (hasUnitName && hasInstallId)
        {
            return Results.Problem(
                detail: "Provide either 'unitName' or 'installId', not both.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        if (!hasUnitName && !hasInstallId)
        {
            return Results.Problem(
                detail: "Either 'unitName' or 'installId' must be provided.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        Services.PackageExportResult? result;

        if (hasUnitName)
        {
            result = await exportService.ExportByUnitNameAsync(
                request.UnitName!,
                request.WithValues,
                cancellationToken);
        }
        else
        {
            result = await exportService.ExportByInstallIdAsync(
                request.InstallId!.Value,
                request.WithValues,
                cancellationToken);
        }

        if (result is null)
        {
            return Results.Problem(
                detail: hasUnitName
                    ? $"No installed package found for unit '{request.UnitName}'."
                    : $"No installed package found for install id '{request.InstallId}'.",
                statusCode: StatusCodes.Status404NotFound);
        }

        return Results.Bytes(
            result.Content,
            contentType: result.ContentType,
            fileDownloadName: result.FileName);
    }
}