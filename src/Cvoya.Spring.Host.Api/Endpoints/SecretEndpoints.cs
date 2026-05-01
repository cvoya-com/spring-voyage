// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Api.Endpoints;

using Cvoya.Spring.Core.Directory;
using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Core.Secrets;
using Cvoya.Spring.Core.Tenancy;
using Cvoya.Spring.Dapr.Data;
using Cvoya.Spring.Dapr.Tenancy;
using Cvoya.Spring.Host.Api.Auth;
using Cvoya.Spring.Host.Api.Models;

using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

/// <summary>
/// Maps the secret-CRUD endpoints for all three scopes:
/// unit (<c>/api/v1/units/{id}/secrets</c>), tenant
/// (<c>/api/v1/tenant/secrets</c>), and platform
/// (<c>/api/v1/platform/secrets</c>).
///
/// <para>
/// <b>Security contract.</b> Plaintext enters the system exclusively via
/// the <c>POST</c> body. It is never echoed in any response body, list
/// entry, or log line. The only way to read a plaintext value back out
/// is via server-side <see cref="ISecretResolver.ResolveAsync"/>.
/// </para>
///
/// <para>
/// <b>RBAC.</b> Role gates are applied at the group level (C1.2 audit):
/// unit-scoped and tenant-scoped groups require <c>TenantOperator</c>;
/// the platform-scoped group requires <c>PlatformOperator</c>. Within
/// each group, <see cref="ISecretAccessPolicy"/> provides a second,
/// scope-shaped gate. The OSS host wires the allow-all default; the
/// private cloud repo registers an implementation that enforces
/// tenant-admin and platform-admin roles.
/// </para>
///
/// <para>
/// <b>Origin safety on DELETE.</b> The registry records every entry with a
/// <see cref="SecretOrigin"/>. The DELETE handler calls
/// <see cref="ISecretStore.DeleteAsync"/> only for
/// <see cref="SecretOrigin.PlatformOwned"/> rows. For
/// <see cref="SecretOrigin.ExternalReference"/> rows it removes the
/// registry pointer only, leaving the externally-managed store key
/// untouched — so a DELETE in the private-cloud Key Vault implementation
/// can never destroy a customer-owned secret.
/// </para>
///
/// <para>
/// <b>Multi-version coexistence (wave 7 A5).</b> Rotation appends a
/// new version instead of replacing. Two additional endpoints per
/// scope expose the new shape: <c>GET /.../secrets/{name}/versions</c>
/// lists per-version metadata; <c>POST /.../secrets/{name}/prune</c>
/// removes older versions with a <c>keep</c> count. DELETE removes
/// every version; the store-layer slot is reclaimed only for
/// platform-owned versions (same safety gate as the single-version
/// path).
/// </para>
/// </summary>
public static class SecretEndpoints
{
    /// <summary>
    /// The canonical owner id used for <see cref="SecretScope.Platform"/>.
    /// A single constant keeps the registry rows addressable by a
    /// well-known key; the private cloud repo is free to partition
    /// further by layering additional logic on top of
    /// <see cref="ISecretAccessPolicy"/>.
    /// </summary>
    public const string PlatformOwnerId = "platform";

    /// <summary>
    /// Registers the unit-scoped, tenant-scoped, and platform-scoped
    /// secret endpoints on the specified endpoint route builder.
    /// </summary>
    /// <param name="app">The endpoint route builder.</param>
    /// <returns>The unit-scoped route group builder for chaining.</returns>
    public static RouteGroupBuilder MapSecretEndpoints(this IEndpointRouteBuilder app)
    {
        var unitGroup = app.MapGroup("/api/v1/tenant/units/{id}/secrets")
            .WithTags("Secrets")
            .RequireAuthorization(RolePolicies.TenantOperator);

        unitGroup.MapGet("/", ListUnitSecretsAsync)
            .WithName("ListUnitSecrets")
            .WithSummary("List secret metadata for a unit. Never returns plaintext or store keys.")
            .Produces<UnitSecretsListResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);

        unitGroup.MapPost("/", CreateUnitSecretAsync)
            .WithName("CreateUnitSecret")
            .WithSummary("Register a unit-scoped secret. Provide exactly one of 'value' (pass-through write) or 'externalStoreKey' (bind existing reference).")
            .Produces<CreateSecretResponse>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status429TooManyRequests);

        unitGroup.MapPut("/{name}", RotateUnitSecretAsync)
            .WithName("RotateUnitSecret")
            .WithSummary("Rotate a unit-scoped secret by appending a new version. Returns the new version number. Prior versions remain resolvable via the pin overload until pruned.")
            .Produces<RotateSecretResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);

        unitGroup.MapGet("/{name}/versions", ListUnitSecretVersionsAsync)
            .WithName("ListUnitSecretVersions")
            .WithSummary("List retained versions for a unit-scoped secret. Metadata only; never returns plaintext or store keys.")
            .Produces<SecretVersionsListResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);

        unitGroup.MapPost("/{name}/prune", PruneUnitSecretAsync)
            .WithName("PruneUnitSecret")
            .WithSummary("Prune older versions of a unit-scoped secret, retaining the N most-recent. 'keep' must be >= 1; the current version is always retained.")
            .Produces<PruneSecretResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);

        unitGroup.MapDelete("/{name}", DeleteUnitSecretAsync)
            .WithName("DeleteUnitSecret")
            .WithSummary("Delete a unit-scoped secret (all versions). Underlying plaintext is removed only for platform-owned versions; external references leave the external store key untouched.")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status500InternalServerError);

        var tenantGroup = app.MapGroup("/api/v1/tenant/secrets")
            .WithTags("Secrets")
            .RequireAuthorization(RolePolicies.TenantOperator);

        tenantGroup.MapGet("/", ListTenantSecretsAsync)
            .WithName("ListTenantSecrets")
            .WithSummary("List secret metadata for the current tenant. Never returns plaintext or store keys.")
            .Produces<SecretsListResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        tenantGroup.MapPost("/", CreateTenantSecretAsync)
            .WithName("CreateTenantSecret")
            .WithSummary("Register a tenant-scoped secret. Provide exactly one of 'value' (pass-through write) or 'externalStoreKey' (bind existing reference).")
            .Produces<CreateSecretResponse>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status429TooManyRequests);

        tenantGroup.MapPut("/{name}", RotateTenantSecretAsync)
            .WithName("RotateTenantSecret")
            .WithSummary("Rotate a tenant-scoped secret by appending a new version. Returns the new version number.")
            .Produces<RotateSecretResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);

        tenantGroup.MapGet("/{name}/versions", ListTenantSecretVersionsAsync)
            .WithName("ListTenantSecretVersions")
            .WithSummary("List retained versions for a tenant-scoped secret. Metadata only; never returns plaintext or store keys.")
            .Produces<SecretVersionsListResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);

        tenantGroup.MapPost("/{name}/prune", PruneTenantSecretAsync)
            .WithName("PruneTenantSecret")
            .WithSummary("Prune older versions of a tenant-scoped secret, retaining the N most-recent. 'keep' must be >= 1.")
            .Produces<PruneSecretResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);

        tenantGroup.MapDelete("/{name}", DeleteTenantSecretAsync)
            .WithName("DeleteTenantSecret")
            .WithSummary("Delete a tenant-scoped secret (all versions). Underlying plaintext is removed only for platform-owned versions; external references leave the external store key untouched.")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status500InternalServerError);

        var platformGroup = app.MapGroup("/api/v1/platform/secrets")
            .WithTags("Secrets")
            .RequireAuthorization(RolePolicies.PlatformOperator);

        platformGroup.MapGet("/", ListPlatformSecretsAsync)
            .WithName("ListPlatformSecrets")
            .WithSummary("List platform-scoped secret metadata. Never returns plaintext or store keys.")
            .Produces<SecretsListResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        platformGroup.MapPost("/", CreatePlatformSecretAsync)
            .WithName("CreatePlatformSecret")
            .WithSummary("Register a platform-scoped secret. Provide exactly one of 'value' (pass-through write) or 'externalStoreKey' (bind existing reference).")
            .Produces<CreateSecretResponse>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status429TooManyRequests);

        platformGroup.MapPut("/{name}", RotatePlatformSecretAsync)
            .WithName("RotatePlatformSecret")
            .WithSummary("Rotate a platform-scoped secret by appending a new version. Returns the new version number.")
            .Produces<RotateSecretResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);

        platformGroup.MapGet("/{name}/versions", ListPlatformSecretVersionsAsync)
            .WithName("ListPlatformSecretVersions")
            .WithSummary("List retained versions for a platform-scoped secret. Metadata only; never returns plaintext or store keys.")
            .Produces<SecretVersionsListResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);

        platformGroup.MapPost("/{name}/prune", PrunePlatformSecretAsync)
            .WithName("PrunePlatformSecret")
            .WithSummary("Prune older versions of a platform-scoped secret, retaining the N most-recent. 'keep' must be >= 1.")
            .Produces<PruneSecretResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);

        platformGroup.MapDelete("/{name}", DeletePlatformSecretAsync)
            .WithName("DeletePlatformSecret")
            .WithSummary("Delete a platform-scoped secret (all versions). Underlying plaintext is removed only for platform-owned versions; external references leave the external store key untouched.")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status500InternalServerError);

        return unitGroup;
    }

    // ------------------------------------------------------------------
    // Unit-scoped handlers
    // ------------------------------------------------------------------

    private static async Task<IResult> ListUnitSecretsAsync(
        string id,
        [FromServices] IDirectoryService directoryService,
        [FromServices] ISecretRegistry registry,
        [FromServices] ISecretAccessPolicy accessPolicy,
        [FromServices] SpringDbContext db,
        CancellationToken cancellationToken)
    {
        if (!await accessPolicy.IsAuthorizedAsync(SecretAccessAction.List, SecretScope.Unit, id, cancellationToken))
        {
            return Forbidden(SecretScope.Unit, SecretAccessAction.List);
        }

        var unitAddress = new Address("unit", id);
        var entry = await directoryService.ResolveAsync(unitAddress, cancellationToken);
        if (entry is null)
        {
            return Results.Problem(
                detail: $"Unit '{id}' not found",
                statusCode: StatusCodes.Status404NotFound);
        }

        // Use the stable ActorId (UUID) as the secret owner key, not the slug
        // from the URL. Slugs are reused when a unit is deleted and recreated;
        // the UUID ensures secrets are scoped to the specific unit instance (#1488).
        var metadata = await ListMetadataAsync(registry, db, SecretScope.Unit, entry.ActorId, cancellationToken);
        return Results.Ok(new UnitSecretsListResponse(metadata));
    }

    private static async Task<IResult> CreateUnitSecretAsync(
        string id,
        CreateSecretRequest request,
        [FromServices] IDirectoryService directoryService,
        [FromServices] ISecretStore store,
        [FromServices] ISecretRegistry registry,
        [FromServices] ISecretAccessPolicy accessPolicy,
        [FromServices] SpringDbContext db,
        [FromServices] IOptions<SecretsOptions> options,
        CancellationToken cancellationToken)
    {
        if (!await accessPolicy.IsAuthorizedAsync(SecretAccessAction.Create, SecretScope.Unit, id, cancellationToken))
        {
            return Forbidden(SecretScope.Unit, SecretAccessAction.Create);
        }

        var validationError = ValidateCreateRequest(request, options.Value);
        if (validationError is not null)
        {
            return validationError;
        }

        var unitAddress = new Address("unit", id);
        var entry = await directoryService.ResolveAsync(unitAddress, cancellationToken);
        if (entry is null)
        {
            return Results.Problem(
                detail: $"Unit '{id}' not found",
                statusCode: StatusCodes.Status404NotFound);
        }

        // Use the stable ActorId (UUID) as the secret owner key, not the slug from
        // the URL (#1488). Pass the slug as locationOwnerId so the 201 Location
        // header contains the human-readable unit id rather than the UUID.
        return await CreateSecretAsync(
            SecretScope.Unit, entry.ActorId, request, store, registry, db, options.Value, cancellationToken,
            locationOwnerId: id);
    }

    private static async Task<IResult> RotateUnitSecretAsync(
        string id,
        string name,
        RotateSecretRequest request,
        [FromServices] IDirectoryService directoryService,
        [FromServices] ISecretStore store,
        [FromServices] ISecretRegistry registry,
        [FromServices] ISecretAccessPolicy accessPolicy,
        [FromServices] IOptions<SecretsOptions> options,
        CancellationToken cancellationToken)
    {
        if (!await accessPolicy.IsAuthorizedAsync(SecretAccessAction.Rotate, SecretScope.Unit, id, cancellationToken))
        {
            return Forbidden(SecretScope.Unit, SecretAccessAction.Rotate);
        }

        var unitAddress = new Address("unit", id);
        var entry = await directoryService.ResolveAsync(unitAddress, cancellationToken);
        if (entry is null)
        {
            return Results.Problem(
                detail: $"Unit '{id}' not found",
                statusCode: StatusCodes.Status404NotFound);
        }

        // Use the stable ActorId (UUID) as the secret owner key (#1488).
        return await RotateSecretAsync(
            SecretScope.Unit, entry.ActorId, name, request, store, registry, options.Value, cancellationToken);
    }

    private static async Task<IResult> ListUnitSecretVersionsAsync(
        string id,
        string name,
        [FromServices] IDirectoryService directoryService,
        [FromServices] ISecretRegistry registry,
        [FromServices] ISecretAccessPolicy accessPolicy,
        CancellationToken cancellationToken)
    {
        if (!await accessPolicy.IsAuthorizedAsync(SecretAccessAction.List, SecretScope.Unit, id, cancellationToken))
        {
            return Forbidden(SecretScope.Unit, SecretAccessAction.List);
        }

        var unitAddress = new Address("unit", id);
        var entry = await directoryService.ResolveAsync(unitAddress, cancellationToken);
        if (entry is null)
        {
            return Results.Problem(
                detail: $"Unit '{id}' not found",
                statusCode: StatusCodes.Status404NotFound);
        }

        // Use the stable ActorId (UUID) as the secret owner key (#1488).
        return await ListVersionsAsync(SecretScope.Unit, entry.ActorId, name, registry, cancellationToken);
    }

    private static async Task<IResult> PruneUnitSecretAsync(
        string id,
        string name,
        int? keep,
        [FromServices] IDirectoryService directoryService,
        [FromServices] ISecretStore store,
        [FromServices] ISecretRegistry registry,
        [FromServices] ISecretAccessPolicy accessPolicy,
        CancellationToken cancellationToken)
    {
        if (!await accessPolicy.IsAuthorizedAsync(SecretAccessAction.Prune, SecretScope.Unit, id, cancellationToken))
        {
            return Forbidden(SecretScope.Unit, SecretAccessAction.Prune);
        }

        var unitAddress = new Address("unit", id);
        var entry = await directoryService.ResolveAsync(unitAddress, cancellationToken);
        if (entry is null)
        {
            return Results.Problem(
                detail: $"Unit '{id}' not found",
                statusCode: StatusCodes.Status404NotFound);
        }

        // Use the stable ActorId (UUID) as the secret owner key (#1488).
        return await PruneSecretAsync(SecretScope.Unit, entry.ActorId, name, keep, store, registry, cancellationToken);
    }

    private static async Task<IResult> DeleteUnitSecretAsync(
        string id,
        string name,
        [FromServices] IDirectoryService directoryService,
        [FromServices] ISecretStore store,
        [FromServices] ISecretRegistry registry,
        [FromServices] ISecretAccessPolicy accessPolicy,
        [FromServices] ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        if (!await accessPolicy.IsAuthorizedAsync(SecretAccessAction.Delete, SecretScope.Unit, id, cancellationToken))
        {
            return Forbidden(SecretScope.Unit, SecretAccessAction.Delete);
        }

        var unitAddress = new Address("unit", id);
        var entry = await directoryService.ResolveAsync(unitAddress, cancellationToken);
        if (entry is null)
        {
            return Results.Problem(
                detail: $"Unit '{id}' not found",
                statusCode: StatusCodes.Status404NotFound);
        }

        // Use the stable ActorId (UUID) as the secret owner key (#1488).
        return await DeleteSecretAsync(
            SecretScope.Unit, entry.ActorId, name, store, registry, loggerFactory, cancellationToken);
    }

    // ------------------------------------------------------------------
    // Tenant-scoped handlers
    // ------------------------------------------------------------------

    private static async Task<IResult> ListTenantSecretsAsync(
        [FromServices] ISecretRegistry registry,
        [FromServices] ISecretAccessPolicy accessPolicy,
        [FromServices] ITenantContext tenantContext,
        [FromServices] SpringDbContext db,
        CancellationToken cancellationToken)
    {
        var ownerId = tenantContext.CurrentTenantId;
        if (!await accessPolicy.IsAuthorizedAsync(SecretAccessAction.List, SecretScope.Tenant, ownerId, cancellationToken))
        {
            return Forbidden(SecretScope.Tenant, SecretAccessAction.List);
        }

        var metadata = await ListMetadataAsync(registry, db, SecretScope.Tenant, ownerId, cancellationToken);
        return Results.Ok(new SecretsListResponse(metadata));
    }

    private static async Task<IResult> CreateTenantSecretAsync(
        CreateSecretRequest request,
        [FromServices] ISecretStore store,
        [FromServices] ISecretRegistry registry,
        [FromServices] ISecretAccessPolicy accessPolicy,
        [FromServices] ITenantContext tenantContext,
        [FromServices] SpringDbContext db,
        [FromServices] IOptions<SecretsOptions> options,
        CancellationToken cancellationToken)
    {
        var ownerId = tenantContext.CurrentTenantId;
        if (!await accessPolicy.IsAuthorizedAsync(SecretAccessAction.Create, SecretScope.Tenant, ownerId, cancellationToken))
        {
            return Forbidden(SecretScope.Tenant, SecretAccessAction.Create);
        }

        var validationError = ValidateCreateRequest(request, options.Value);
        if (validationError is not null)
        {
            return validationError;
        }

        return await CreateSecretAsync(
            SecretScope.Tenant, ownerId, request, store, registry, db, options.Value, cancellationToken);
    }

    private static async Task<IResult> RotateTenantSecretAsync(
        string name,
        RotateSecretRequest request,
        [FromServices] ISecretStore store,
        [FromServices] ISecretRegistry registry,
        [FromServices] ISecretAccessPolicy accessPolicy,
        [FromServices] ITenantContext tenantContext,
        [FromServices] IOptions<SecretsOptions> options,
        CancellationToken cancellationToken)
    {
        var ownerId = tenantContext.CurrentTenantId;
        if (!await accessPolicy.IsAuthorizedAsync(SecretAccessAction.Rotate, SecretScope.Tenant, ownerId, cancellationToken))
        {
            return Forbidden(SecretScope.Tenant, SecretAccessAction.Rotate);
        }

        return await RotateSecretAsync(
            SecretScope.Tenant, ownerId, name, request, store, registry, options.Value, cancellationToken);
    }

    private static async Task<IResult> ListTenantSecretVersionsAsync(
        string name,
        [FromServices] ISecretRegistry registry,
        [FromServices] ISecretAccessPolicy accessPolicy,
        [FromServices] ITenantContext tenantContext,
        CancellationToken cancellationToken)
    {
        var ownerId = tenantContext.CurrentTenantId;
        if (!await accessPolicy.IsAuthorizedAsync(SecretAccessAction.List, SecretScope.Tenant, ownerId, cancellationToken))
        {
            return Forbidden(SecretScope.Tenant, SecretAccessAction.List);
        }

        return await ListVersionsAsync(SecretScope.Tenant, ownerId, name, registry, cancellationToken);
    }

    private static async Task<IResult> PruneTenantSecretAsync(
        string name,
        int? keep,
        [FromServices] ISecretStore store,
        [FromServices] ISecretRegistry registry,
        [FromServices] ISecretAccessPolicy accessPolicy,
        [FromServices] ITenantContext tenantContext,
        CancellationToken cancellationToken)
    {
        var ownerId = tenantContext.CurrentTenantId;
        if (!await accessPolicy.IsAuthorizedAsync(SecretAccessAction.Prune, SecretScope.Tenant, ownerId, cancellationToken))
        {
            return Forbidden(SecretScope.Tenant, SecretAccessAction.Prune);
        }

        return await PruneSecretAsync(SecretScope.Tenant, ownerId, name, keep, store, registry, cancellationToken);
    }

    private static async Task<IResult> DeleteTenantSecretAsync(
        string name,
        [FromServices] ISecretStore store,
        [FromServices] ISecretRegistry registry,
        [FromServices] ISecretAccessPolicy accessPolicy,
        [FromServices] ITenantContext tenantContext,
        [FromServices] ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        var ownerId = tenantContext.CurrentTenantId;
        if (!await accessPolicy.IsAuthorizedAsync(SecretAccessAction.Delete, SecretScope.Tenant, ownerId, cancellationToken))
        {
            return Forbidden(SecretScope.Tenant, SecretAccessAction.Delete);
        }

        return await DeleteSecretAsync(
            SecretScope.Tenant, ownerId, name, store, registry, loggerFactory, cancellationToken);
    }

    // ------------------------------------------------------------------
    // Platform-scoped handlers
    // ------------------------------------------------------------------

    private static async Task<IResult> ListPlatformSecretsAsync(
        [FromServices] ISecretRegistry registry,
        [FromServices] ISecretAccessPolicy accessPolicy,
        [FromServices] SpringDbContext db,
        CancellationToken cancellationToken)
    {
        if (!await accessPolicy.IsAuthorizedAsync(SecretAccessAction.List, SecretScope.Platform, PlatformOwnerId, cancellationToken))
        {
            return Forbidden(SecretScope.Platform, SecretAccessAction.List);
        }

        var metadata = await ListMetadataAsync(registry, db, SecretScope.Platform, PlatformOwnerId, cancellationToken);
        return Results.Ok(new SecretsListResponse(metadata));
    }

    private static async Task<IResult> CreatePlatformSecretAsync(
        CreateSecretRequest request,
        [FromServices] ISecretStore store,
        [FromServices] ISecretRegistry registry,
        [FromServices] ISecretAccessPolicy accessPolicy,
        [FromServices] SpringDbContext db,
        [FromServices] IOptions<SecretsOptions> options,
        CancellationToken cancellationToken)
    {
        if (!await accessPolicy.IsAuthorizedAsync(SecretAccessAction.Create, SecretScope.Platform, PlatformOwnerId, cancellationToken))
        {
            return Forbidden(SecretScope.Platform, SecretAccessAction.Create);
        }

        var validationError = ValidateCreateRequest(request, options.Value);
        if (validationError is not null)
        {
            return validationError;
        }

        return await CreateSecretAsync(
            SecretScope.Platform, PlatformOwnerId, request, store, registry, db, options.Value, cancellationToken);
    }

    private static async Task<IResult> RotatePlatformSecretAsync(
        string name,
        RotateSecretRequest request,
        [FromServices] ISecretStore store,
        [FromServices] ISecretRegistry registry,
        [FromServices] ISecretAccessPolicy accessPolicy,
        [FromServices] IOptions<SecretsOptions> options,
        CancellationToken cancellationToken)
    {
        if (!await accessPolicy.IsAuthorizedAsync(SecretAccessAction.Rotate, SecretScope.Platform, PlatformOwnerId, cancellationToken))
        {
            return Forbidden(SecretScope.Platform, SecretAccessAction.Rotate);
        }

        return await RotateSecretAsync(
            SecretScope.Platform, PlatformOwnerId, name, request, store, registry, options.Value, cancellationToken);
    }

    private static async Task<IResult> ListPlatformSecretVersionsAsync(
        string name,
        [FromServices] ISecretRegistry registry,
        [FromServices] ISecretAccessPolicy accessPolicy,
        CancellationToken cancellationToken)
    {
        if (!await accessPolicy.IsAuthorizedAsync(SecretAccessAction.List, SecretScope.Platform, PlatformOwnerId, cancellationToken))
        {
            return Forbidden(SecretScope.Platform, SecretAccessAction.List);
        }

        return await ListVersionsAsync(SecretScope.Platform, PlatformOwnerId, name, registry, cancellationToken);
    }

    private static async Task<IResult> PrunePlatformSecretAsync(
        string name,
        int? keep,
        [FromServices] ISecretStore store,
        [FromServices] ISecretRegistry registry,
        [FromServices] ISecretAccessPolicy accessPolicy,
        CancellationToken cancellationToken)
    {
        if (!await accessPolicy.IsAuthorizedAsync(SecretAccessAction.Prune, SecretScope.Platform, PlatformOwnerId, cancellationToken))
        {
            return Forbidden(SecretScope.Platform, SecretAccessAction.Prune);
        }

        return await PruneSecretAsync(SecretScope.Platform, PlatformOwnerId, name, keep, store, registry, cancellationToken);
    }

    private static async Task<IResult> DeletePlatformSecretAsync(
        string name,
        [FromServices] ISecretStore store,
        [FromServices] ISecretRegistry registry,
        [FromServices] ISecretAccessPolicy accessPolicy,
        [FromServices] ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        if (!await accessPolicy.IsAuthorizedAsync(SecretAccessAction.Delete, SecretScope.Platform, PlatformOwnerId, cancellationToken))
        {
            return Forbidden(SecretScope.Platform, SecretAccessAction.Delete);
        }

        return await DeleteSecretAsync(
            SecretScope.Platform, PlatformOwnerId, name, store, registry, loggerFactory, cancellationToken);
    }

    // ------------------------------------------------------------------
    // Shared helpers
    // ------------------------------------------------------------------

    private static IResult? ValidateCreateRequest(CreateSecretRequest request, SecretsOptions options)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
        {
            return Results.Problem(
                detail: "Request body must include a non-empty 'name'.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        var hasValue = !string.IsNullOrEmpty(request.Value);
        var hasExternal = !string.IsNullOrWhiteSpace(request.ExternalStoreKey);

        if (hasValue == hasExternal)
        {
            return Results.Problem(
                detail: "Request body must include exactly one of 'value' or 'externalStoreKey'.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        if (hasValue && !options.AllowPassThroughWrites)
        {
            return Results.Problem(
                detail: "Pass-through secret writes are disabled (Secrets:AllowPassThroughWrites = false).",
                statusCode: StatusCodes.Status403Forbidden);
        }

        if (hasExternal && !options.AllowExternalReferenceWrites)
        {
            return Results.Problem(
                detail: "External-reference secret writes are disabled (Secrets:AllowExternalReferenceWrites = false).",
                statusCode: StatusCodes.Status403Forbidden);
        }

        return null;
    }

    private static IResult? ValidateRotateRequest(RotateSecretRequest request, SecretsOptions options)
    {
        var hasValue = !string.IsNullOrEmpty(request.Value);
        var hasExternal = !string.IsNullOrWhiteSpace(request.ExternalStoreKey);

        if (hasValue == hasExternal)
        {
            return Results.Problem(
                detail: "Request body must include exactly one of 'value' or 'externalStoreKey'.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        if (hasValue && !options.AllowPassThroughWrites)
        {
            return Results.Problem(
                detail: "Pass-through secret writes are disabled (Secrets:AllowPassThroughWrites = false).",
                statusCode: StatusCodes.Status403Forbidden);
        }

        if (hasExternal && !options.AllowExternalReferenceWrites)
        {
            return Results.Problem(
                detail: "External-reference secret writes are disabled (Secrets:AllowExternalReferenceWrites = false).",
                statusCode: StatusCodes.Status403Forbidden);
        }

        return null;
    }

    private static async Task<IResult> RotateSecretAsync(
        SecretScope scope,
        string ownerId,
        string name,
        RotateSecretRequest request,
        ISecretStore store,
        ISecretRegistry registry,
        SecretsOptions options,
        CancellationToken cancellationToken)
    {
        var validationError = ValidateRotateRequest(request, options);
        if (validationError is not null)
        {
            return validationError;
        }

        var secretRef = new SecretRef(scope, ownerId, name);

        // Pre-flight check: rotate requires an existing entry. We look
        // up explicitly so we can return 404 with a ProblemDetails body
        // — the registry throws InvalidOperationException otherwise.
        var pointer = await registry.LookupAsync(secretRef, cancellationToken);
        if (pointer is null)
        {
            return Results.Problem(
                detail: $"Secret '{name}' not found for {scope} '{ownerId}'. Use POST to create.",
                statusCode: StatusCodes.Status404NotFound);
        }

        var hasValue = !string.IsNullOrEmpty(request.Value);
        string newStoreKey;
        SecretOrigin newOrigin;
        if (hasValue)
        {
            newStoreKey = await store.WriteAsync(request.Value!, cancellationToken);
            newOrigin = SecretOrigin.PlatformOwned;
        }
        else
        {
            newStoreKey = request.ExternalStoreKey!;
            newOrigin = SecretOrigin.ExternalReference;
        }

        SecretRotation rotation;
        try
        {
            // A5 retention: rotation appends; prior versions stay in
            // place. The old-slot delete delegate is retained on the
            // interface for compatibility but is never invoked by the
            // registry — we pass null so there is no ambiguity.
            rotation = await registry.RotateAsync(
                secretRef,
                newStoreKey,
                newOrigin,
                deletePreviousStoreKeyAsync: null,
                cancellationToken);
        }
        catch (InvalidOperationException)
        {
            // Race between the LookupAsync pre-flight and the rotate —
            // the entry was removed concurrently. Clean up the orphaned
            // new slot (if we wrote one) and surface 404.
            if (newOrigin == SecretOrigin.PlatformOwned)
            {
                try
                {
                    await store.DeleteAsync(newStoreKey, CancellationToken.None);
                }
                catch
                {
                    // Swallow — reconciliation sweep handles orphans.
                }
            }
            return Results.Problem(
                detail: $"Secret '{name}' not found for {scope} '{ownerId}'. Use POST to create.",
                statusCode: StatusCodes.Status404NotFound);
        }
        catch
        {
            if (newOrigin == SecretOrigin.PlatformOwned)
            {
                try
                {
                    await store.DeleteAsync(newStoreKey, CancellationToken.None);
                }
                catch
                {
                    // Reconciliation handles orphans.
                }
            }
            throw;
        }

        return Results.Ok(new RotateSecretResponse(name, scope, rotation.ToVersion));
    }

    private static async Task<IResult> ListVersionsAsync(
        SecretScope scope,
        string ownerId,
        string name,
        ISecretRegistry registry,
        CancellationToken cancellationToken)
    {
        var secretRef = new SecretRef(scope, ownerId, name);
        var versions = await registry.ListVersionsAsync(secretRef, cancellationToken);
        if (versions.Count == 0)
        {
            return Results.Problem(
                detail: $"Secret '{name}' not found for {scope} '{ownerId}'.",
                statusCode: StatusCodes.Status404NotFound);
        }

        var entries = versions
            .Select(v => new SecretVersionEntry(v.Version, v.Origin, v.CreatedAt, v.IsCurrent))
            .ToList();

        return Results.Ok(new SecretVersionsListResponse(name, scope, entries));
    }

    private static async Task<IResult> PruneSecretAsync(
        SecretScope scope,
        string ownerId,
        string name,
        int? keep,
        ISecretStore store,
        ISecretRegistry registry,
        CancellationToken cancellationToken)
    {
        if (keep is null || keep.Value < 1)
        {
            return Results.Problem(
                detail: "Query parameter 'keep' must be a positive integer (>= 1).",
                statusCode: StatusCodes.Status400BadRequest);
        }

        var secretRef = new SecretRef(scope, ownerId, name);
        // 404 if the chain does not exist at all — prune is not a
        // "create if missing" primitive. Listing versions is the
        // cheapest existence check that also gives us the count.
        var versions = await registry.ListVersionsAsync(secretRef, cancellationToken);
        if (versions.Count == 0)
        {
            return Results.Problem(
                detail: $"Secret '{name}' not found for {scope} '{ownerId}'.",
                statusCode: StatusCodes.Status404NotFound);
        }

        // Reclaim platform-owned store slots for pruned versions;
        // external references are left untouched. The delegate fires
        // per pruned platform-owned row (see EfSecretRegistry.PruneAsync).
        var pruned = await registry.PruneAsync(
            secretRef,
            keep.Value,
            async (oldKey, ct) =>
            {
                try
                {
                    await store.DeleteAsync(oldKey, ct);
                }
                catch
                {
                    // Best-effort reclaim; orphaned slots are handled by
                    // the same reconciliation sweep that covers rotate
                    // failures (see follow-up issue for the reconciler).
                }
            },
            cancellationToken);

        return Results.Ok(new PruneSecretResponse(name, scope, keep.Value, pruned));
    }

    private static async Task<IResult> CreateSecretAsync(
        SecretScope scope,
        string ownerId,
        CreateSecretRequest request,
        ISecretStore store,
        ISecretRegistry registry,
        SpringDbContext db,
        SecretsOptions options,
        CancellationToken cancellationToken,
        string? locationOwnerId = null)
    {
        // locationOwnerId is the slug used to build the Location header URL for
        // unit-scoped secrets. For tenant- and platform-scoped secrets the owner
        // id already matches the URL segment, so the parameter defaults to null
        // (use ownerId). See #1488 for why unit secrets use a UUID ownerId but
        // a slug in the URL.
        var urlOwnerId = locationOwnerId ?? ownerId;

        var max = options.MaxSecretsPerOwner;
        if (max > 0)
        {
            var existing = await registry.ListAsync(scope, ownerId, cancellationToken);
            var alreadyHas = existing.Any(r => string.Equals(r.Name, request.Name, StringComparison.Ordinal));
            if (!alreadyHas && existing.Count >= max)
            {
                return Results.Problem(
                    title: "Too many secrets",
                    detail: $"{scope} owner '{ownerId}' already holds {existing.Count} secrets; limit is {max} (Secrets:MaxSecretsPerOwner).",
                    statusCode: StatusCodes.Status429TooManyRequests);
            }
        }

        var hasValue = !string.IsNullOrEmpty(request.Value);
        var secretRef = new SecretRef(scope, ownerId, request.Name);

        string storeKey;
        SecretOrigin origin;
        if (hasValue)
        {
            storeKey = await store.WriteAsync(request.Value!, cancellationToken);
            origin = SecretOrigin.PlatformOwned;
        }
        else
        {
            storeKey = request.ExternalStoreKey!;
            origin = SecretOrigin.ExternalReference;
        }

        try
        {
            await registry.RegisterAsync(secretRef, storeKey, origin, cancellationToken);
        }
        catch
        {
            if (origin == SecretOrigin.PlatformOwned)
            {
                try
                {
                    await store.DeleteAsync(storeKey, CancellationToken.None);
                }
                catch
                {
                    // Swallow cleanup failure — the registry exception is
                    // the primary error; a reconciliation sweep handles
                    // orphans.
                }
            }
            throw;
        }

        var row = await db.SecretRegistryEntries
            .AsNoTracking()
            .Where(e => e.Scope == scope && e.OwnerId == ownerId && e.Name == request.Name)
            .OrderByDescending(e => e.Version)
            .Select(e => new { e.CreatedAt })
            .FirstOrDefaultAsync(cancellationToken);

        var createdAt = row?.CreatedAt ?? DateTimeOffset.UtcNow;
        var location = BuildResourceLocation(scope, urlOwnerId, request.Name);
        return Results.Created(
            location,
            new CreateSecretResponse(request.Name, scope, createdAt));
    }

    private static async Task<IResult> DeleteSecretAsync(
        SecretScope scope,
        string ownerId,
        string name,
        ISecretStore store,
        ISecretRegistry registry,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        var secretRef = new SecretRef(scope, ownerId, name);

        // Enumerate every version so we can reclaim each platform-owned
        // store slot before removing the chain from the registry. A
        // missing chain is 404 — prior semantics.
        var versions = await registry.ListVersionsAsync(secretRef, cancellationToken);
        if (versions.Count == 0)
        {
            return Results.Problem(
                detail: $"Secret '{name}' not found for {scope} '{ownerId}'",
                statusCode: StatusCodes.Status404NotFound);
        }

        // Fetch every row's pointer so we can delete each platform-
        // owned slot before touching the registry chain. We resolve
        // per-version pointers via LookupWithVersionAsync to keep the
        // scan tenant-filtered and bounded to the chain we already
        // saw in ListVersionsAsync.
        var platformSlots = new List<string>();
        foreach (var version in versions)
        {
            var pointer = await registry.LookupWithVersionAsync(secretRef, version.Version, cancellationToken);
            if (pointer is not null && pointer.Value.Pointer.Origin == SecretOrigin.PlatformOwned)
            {
                platformSlots.Add(pointer.Value.Pointer.StoreKey);
            }
        }

        foreach (var storeKey in platformSlots)
        {
            try
            {
                await store.DeleteAsync(storeKey, cancellationToken);
            }
            catch (Exception ex)
            {
                var logger = loggerFactory.CreateLogger("Cvoya.Spring.Host.Api.Endpoints.SecretEndpoints");
                logger.LogError(ex,
                    "Store-delete failed for {Scope} secret '{Owner}/{Name}' version slot; registry row retained so operator can retry.",
                    scope, ownerId, name);
                return Results.Problem(
                    title: "Secret store delete failed",
                    detail: $"Underlying store rejected the delete for '{name}'. The secret remains resolvable; retry the DELETE. Underlying error: {ex.Message}",
                    statusCode: StatusCodes.Status500InternalServerError);
            }
        }

        try
        {
            await registry.DeleteAsync(secretRef, cancellationToken);
        }
        catch (Exception ex)
        {
            var logger = loggerFactory.CreateLogger("Cvoya.Spring.Host.Api.Endpoints.SecretEndpoints");
            logger.LogError(ex,
                "Registry-delete failed for {Scope} secret '{Owner}/{Name}' after successful store deletes; retry will complete.",
                scope, ownerId, name);
            return Results.Problem(
                title: "Secret registry delete failed",
                detail: $"Store value for '{name}' was removed but the registry entry could not be cleared. Retry the DELETE.",
                statusCode: StatusCodes.Status500InternalServerError);
        }

        return Results.NoContent();
    }

    private static async Task<IReadOnlyList<SecretMetadata>> ListMetadataAsync(
        ISecretRegistry registry,
        SpringDbContext db,
        SecretScope scope,
        string ownerId,
        CancellationToken cancellationToken)
    {
        // The registry ListAsync returns one SecretRef per named secret
        // (collapsing the per-version rows). We join against the raw
        // table to surface the earliest (initial) CreatedAt for each
        // name — rotating a secret should not change the original
        // creation timestamp surfaced in the list view.
        var refs = await registry.ListAsync(scope, ownerId, cancellationToken);

        var refNames = refs.Select(r => r.Name).ToHashSet(StringComparer.Ordinal);
        var timestamps = await db.SecretRegistryEntries
            .AsNoTracking()
            .Where(e => e.Scope == scope && e.OwnerId == ownerId)
            .Select(e => new { e.Name, e.CreatedAt })
            .ToListAsync(cancellationToken);

        var timestampsByName = timestamps
            .Where(t => refNames.Contains(t.Name))
            .GroupBy(t => t.Name)
            .ToDictionary(g => g.Key, g => g.Min(t => t.CreatedAt), StringComparer.Ordinal);

        return refs
            .Select(r => new SecretMetadata(
                r.Name,
                r.Scope,
                timestampsByName.GetValueOrDefault(r.Name, DateTimeOffset.MinValue)))
            .OrderBy(m => m.Name, StringComparer.Ordinal)
            .ToList();
    }

    private static string BuildResourceLocation(SecretScope scope, string ownerId, string name) => scope switch
    {
        SecretScope.Unit => $"/api/v1/tenant/units/{ownerId}/secrets/{name}",
        SecretScope.Tenant => $"/api/v1/tenant/secrets/{name}",
        SecretScope.Platform => $"/api/v1/platform/secrets/{name}",
        _ => $"/api/v1/secrets/{name}",
    };

    private static IResult Forbidden(SecretScope scope, SecretAccessAction action) =>
        Results.Problem(
            title: "Forbidden",
            detail: $"Not authorized to {action} secrets in scope '{scope}'.",
            statusCode: StatusCodes.Status403Forbidden);
}