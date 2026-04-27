// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Api.Endpoints;

using Cvoya.Spring.Core.Tenancy;
using Cvoya.Spring.Host.Api.Auth;
using Cvoya.Spring.Host.Api.Models;

using Microsoft.AspNetCore.Mvc;

/// <summary>
/// Maps the platform-tenant management surface at
/// <c>/api/v1/platform/tenants</c> (C1.2d / #1260). Every route is gated
/// to <see cref="Cvoya.Spring.Core.Security.PlatformRoles.PlatformOperator"/>.
/// </summary>
/// <remarks>
/// <para>
/// These endpoints back the CLI's <c>spring tenant …</c> verbs, the
/// portal's tenant management view (when it lands), and the cloud
/// overlay's self-onboarding flow (cvoya-com/spring#825). The OSS
/// auth-handler grants every authenticated caller the
/// <c>PlatformOperator</c> role so this surface is fully usable in
/// single-user OSS deployments; the cloud overlay's
/// <see cref="IRoleClaimSource"/> scopes the granted subset per identity
/// and 403s callers without the role.
/// </para>
/// <para>
/// Tenant records are global by design — operations here legitimately
/// cross the per-tenant query filter that protects the rest of the
/// platform. The <see cref="ITenantRegistry"/> implementation opens an
/// audited <see cref="ITenantScopeBypass"/> scope around every call so
/// the structured log captures the cross-tenant access.
/// </para>
/// </remarks>
public static class PlatformTenantsEndpoints
{
    /// <summary>
    /// Registers <c>/api/v1/platform/tenants/*</c> on the supplied route
    /// builder. Returns the group so callers can apply additional
    /// middleware if required.
    /// </summary>
    public static RouteGroupBuilder MapPlatformTenantsEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/platform/tenants")
            .WithTags("PlatformTenants")
            .RequireAuthorization(RolePolicies.PlatformOperator);

        group.MapGet("/", ListAsync)
            .WithName("ListPlatformTenants")
            .WithSummary("List every tenant in the platform registry")
            .Produces<TenantsListResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        group.MapPost("/", CreateAsync)
            .WithName("CreatePlatformTenant")
            .WithSummary("Create a new tenant record")
            .Produces<TenantResponse>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status409Conflict);

        group.MapGet("/{id}", GetAsync)
            .WithName("GetPlatformTenant")
            .WithSummary("Get a single tenant record by id")
            .Produces<TenantResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapPatch("/{id}", UpdateAsync)
            .WithName("UpdatePlatformTenant")
            .WithSummary("Update a tenant record's mutable fields (display name)")
            .Produces<TenantResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapDelete("/{id}", DeleteAsync)
            .WithName("DeletePlatformTenant")
            .WithSummary("Soft-delete a tenant record")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);

        return group;
    }

    private static async Task<IResult> ListAsync(
        [FromServices] ITenantRegistry registry,
        CancellationToken cancellationToken)
    {
        var items = await registry.ListAsync(cancellationToken);
        var responses = items.Select(Project).ToList();
        return Results.Ok(new TenantsListResponse(responses));
    }

    private static async Task<IResult> GetAsync(
        string id,
        [FromServices] ITenantRegistry registry,
        CancellationToken cancellationToken)
    {
        var record = await registry.GetAsync(id, cancellationToken);
        return record is null
            ? TenantNotFound(id)
            : Results.Ok(Project(record));
    }

    private static async Task<IResult> CreateAsync(
        CreateTenantRequest request,
        [FromServices] ITenantRegistry registry,
        CancellationToken cancellationToken)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.Id))
        {
            return Results.Problem(
                title: "Invalid tenant request",
                detail: "Request body must include a non-empty 'id'.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        TenantRecord record;
        try
        {
            record = await registry.CreateAsync(request.Id, request.DisplayName, cancellationToken);
        }
        catch (ArgumentException ex)
        {
            return Results.Problem(
                title: "Invalid tenant request",
                detail: ex.Message,
                statusCode: StatusCodes.Status400BadRequest);
        }
        catch (InvalidOperationException ex)
        {
            return Results.Problem(
                title: "Tenant already exists",
                detail: ex.Message,
                statusCode: StatusCodes.Status409Conflict);
        }

        return Results.Created($"/api/v1/platform/tenants/{record.Id}", Project(record));
    }

    private static async Task<IResult> UpdateAsync(
        string id,
        UpdateTenantRequest request,
        [FromServices] ITenantRegistry registry,
        CancellationToken cancellationToken)
    {
        if (request is null)
        {
            return Results.Problem(
                title: "Invalid tenant request",
                detail: "Request body must be present.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        var record = await registry.UpdateAsync(id, request.DisplayName, cancellationToken);
        return record is null
            ? TenantNotFound(id)
            : Results.Ok(Project(record));
    }

    private static async Task<IResult> DeleteAsync(
        string id,
        [FromServices] ITenantRegistry registry,
        CancellationToken cancellationToken)
    {
        var deleted = await registry.DeleteAsync(id, cancellationToken);
        return deleted ? Results.NoContent() : TenantNotFound(id);
    }

    private static TenantResponse Project(TenantRecord record) =>
        new(record.Id, record.DisplayName, record.State, record.CreatedAt, record.UpdatedAt);

    private static IResult TenantNotFound(string id) =>
        Results.Problem(
            title: "Tenant not found",
            detail: $"Tenant '{id}' was not found.",
            statusCode: StatusCodes.Status404NotFound);
}