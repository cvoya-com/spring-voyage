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
        if (!Cvoya.Spring.Core.Identifiers.GuidFormatter.TryParse(id, out var idGuid))
        {
            return TenantNotFound(id);
        }
        var record = await registry.GetAsync(idGuid, cancellationToken);
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
        if (!Cvoya.Spring.Core.Identifiers.GuidFormatter.TryParse(request.Id, out var requestIdGuid))
        {
            return Results.Problem(
                title: "Invalid tenant request",
                detail: $"Tenant id '{request.Id}' is not a valid Guid.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        // #1632: an explicitly supplied display name must clear the same
        // Guid-shape / control-char gate every other entity surface uses.
        // null / whitespace is the "default to the id" signal documented on
        // CreateTenantRequest — leave that path unchanged.
        if (!string.IsNullOrWhiteSpace(request.DisplayName))
        {
            var displayNameProblem = DisplayNameProblems.ValidateOrProblem(request.DisplayName);
            if (displayNameProblem is not null)
            {
                return displayNameProblem;
            }
        }

        TenantRecord record;
        try
        {
            record = await registry.CreateAsync(requestIdGuid, request.DisplayName, cancellationToken);
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

        return Results.Created($"/api/v1/platform/tenants/{Cvoya.Spring.Core.Identifiers.GuidFormatter.Format(record.Id)}", Project(record));
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

        if (!Cvoya.Spring.Core.Identifiers.GuidFormatter.TryParse(id, out var idGuid))
        {
            return TenantNotFound(id);
        }

        // #1632: see CreateAsync — empty / whitespace is the "fall back to
        // the tenant id" signal documented on UpdateTenantRequest, so the
        // validator only fires on a meaningful value.
        if (!string.IsNullOrWhiteSpace(request.DisplayName))
        {
            var displayNameProblem = DisplayNameProblems.ValidateOrProblem(request.DisplayName);
            if (displayNameProblem is not null)
            {
                return displayNameProblem;
            }
        }

        var record = await registry.UpdateAsync(idGuid, request.DisplayName, cancellationToken);
        return record is null
            ? TenantNotFound(id)
            : Results.Ok(Project(record));
    }

    private static async Task<IResult> DeleteAsync(
        string id,
        [FromServices] ITenantRegistry registry,
        CancellationToken cancellationToken)
    {
        if (!Cvoya.Spring.Core.Identifiers.GuidFormatter.TryParse(id, out var idGuid))
        {
            return TenantNotFound(id);
        }
        var deleted = await registry.DeleteAsync(idGuid, cancellationToken);
        return deleted ? Results.NoContent() : TenantNotFound(id);
    }

    private static TenantResponse Project(TenantRecord record) =>
        new(Cvoya.Spring.Core.Identifiers.GuidFormatter.Format(record.Id), record.DisplayName, record.State, record.CreatedAt, record.UpdatedAt);

    private static IResult TenantNotFound(string id) =>
        Results.Problem(
            title: "Tenant not found",
            detail: $"Tenant '{id}' was not found.",
            statusCode: StatusCodes.Status404NotFound);
}