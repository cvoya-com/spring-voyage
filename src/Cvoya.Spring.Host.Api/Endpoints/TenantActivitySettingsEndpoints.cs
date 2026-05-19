// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Api.Endpoints;

using Cvoya.Spring.Core.Capabilities;
using Cvoya.Spring.Core.Tenancy;
using Cvoya.Spring.Host.Api.Models;

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

/// <summary>
/// Maps the tenant activity-capture settings API surface (issue #2492).
/// Exposes <c>GET</c> and <c>PATCH</c> on
/// <c>/api/v1/tenant/activity/settings</c>: capture level + retention
/// horizon. CLI verbs <c>spring tenant activity get</c> / <c>set</c>
/// consume these via the generated Kiota client.
/// </summary>
public static class TenantActivitySettingsEndpoints
{
    /// <summary>Registers the tenant activity-settings endpoints.</summary>
    public static RouteGroupBuilder MapTenantActivitySettingsEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/tenant/activity/settings")
            .WithTags("Tenant");

        group.MapGet("/", GetSettingsAsync)
            .WithName("GetTenantActivitySettings")
            .WithSummary("Get the tenant's activity-capture settings (level + retention).")
            .Produces<TenantActivitySettingsDto>(StatusCodes.Status200OK);

        group.MapPatch("/", UpdateSettingsAsync)
            .WithName("UpdateTenantActivitySettings")
            .WithSummary("Update the tenant's activity-capture settings.")
            .Produces<TenantActivitySettingsDto>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest);

        return group;
    }

    private static async Task<IResult> GetSettingsAsync(
        [FromServices] ITenantActivitySettings settings,
        [FromServices] ITenantContext tenantContext,
        CancellationToken cancellationToken)
    {
        var snapshot = await settings.GetAsync(tenantContext.CurrentTenantId, cancellationToken);
        return Results.Ok(TenantActivitySettingsDto.FromSnapshot(snapshot));
    }

    private static async Task<IResult> UpdateSettingsAsync(
        [FromBody] UpdateTenantActivitySettingsRequest request,
        [FromServices] ITenantActivitySettings settings,
        [FromServices] ITenantContext tenantContext,
        CancellationToken cancellationToken)
    {
        ActivityCaptureLevel? level = null;
        if (!string.IsNullOrEmpty(request.Level))
        {
            if (!Enum.TryParse<ActivityCaptureLevel>(request.Level, ignoreCase: true, out var parsed))
            {
                return Results.Problem(
                    detail: $"Invalid capture level '{request.Level}'. Expected one of: off, summary, full.",
                    statusCode: StatusCodes.Status400BadRequest);
            }
            level = parsed;
        }

        if (request.RetentionDays is { } days && days <= 0)
        {
            return Results.Problem(
                detail: "retention_days must be a positive integer.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        var snapshot = await settings.SetAsync(
            tenantContext.CurrentTenantId,
            level,
            request.RetentionDays,
            cancellationToken);
        return Results.Ok(TenantActivitySettingsDto.FromSnapshot(snapshot));
    }
}
