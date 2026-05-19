// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Api.Endpoints;

using System.Collections.Generic;

using Cvoya.Spring.Core.Capabilities;
using Cvoya.Spring.Core.Tenancy;
using Cvoya.Spring.Dapr.Observability;
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

        // #2503: the forwarder status endpoint sits on the same path so
        // CLI / portal consumers find it alongside the rest of the
        // activity surface.
        group.MapGet("/forward-status", GetForwardStatusAsync)
            .WithName("GetTenantActivityForwardStatus")
            .WithSummary("Get the most recent external-forward attempt result for the tenant (#2503).")
            .Produces<TenantActivityForwardStatusDto>(StatusCodes.Status200OK);

        return group;
    }

    private static Task<IResult> GetForwardStatusAsync(
        [FromServices] ForwardingOtlpIngestServiceDecorator forwarder,
        [FromServices] ITenantContext tenantContext)
    {
        var tenantId = tenantContext.CurrentTenantId;
        if (!forwarder.Status.TryGetValue(tenantId, out var snapshot))
        {
            // No attempt yet — surface "disabled" so the CLI / portal
            // render a stable shape rather than a 404.
            return Task.FromResult(Results.Ok(new TenantActivityForwardStatusDto(
                Kind: "disabled",
                ObservedAt: null,
                Message: null)));
        }
        return Task.FromResult(Results.Ok(new TenantActivityForwardStatusDto(
            Kind: snapshot.Kind.ToString().ToLowerInvariant(),
            ObservedAt: snapshot.ObservedAt,
            Message: snapshot.Message)));
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

        var forwardUpdate = BuildForwardUpdate(request);

        var snapshot = await settings.SetAsync(
            tenantContext.CurrentTenantId,
            level,
            request.RetentionDays,
            externalForward: forwardUpdate,
            cancellationToken: cancellationToken);
        return Results.Ok(TenantActivitySettingsDto.FromSnapshot(snapshot));
    }

    /// <summary>
    /// Projects the wire-shape forwarding block onto the domain update
    /// sentinel. Empty / null endpoint with <c>clear=true</c> clears the
    /// stored block; otherwise we set the block to the request values.
    /// </summary>
    internal static ExternalForwardUpdate? BuildForwardUpdate(UpdateTenantActivitySettingsRequest request)
    {
        if (request.ExternalForward is null)
        {
            // Leave the existing block untouched.
            return null;
        }
        if (request.ExternalForward.Clear == true)
        {
            return ExternalForwardUpdate.Clear;
        }
        if (string.IsNullOrEmpty(request.ExternalForward.Endpoint))
        {
            return null;
        }
        var protocol = string.IsNullOrEmpty(request.ExternalForward.Protocol)
            ? "http/protobuf"
            : request.ExternalForward.Protocol;
        var headers = request.ExternalForward.Headers
            ?? new Dictionary<string, string>(StringComparer.Ordinal);
        return ExternalForwardUpdate.Set(new ExternalOtelForwardConfig(
            Endpoint: request.ExternalForward.Endpoint,
            Protocol: protocol,
            Headers: headers,
            Enabled: request.ExternalForward.Enabled ?? true));
    }
}
