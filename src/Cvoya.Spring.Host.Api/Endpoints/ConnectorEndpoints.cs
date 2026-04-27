// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Api.Endpoints;

using Cvoya.Spring.Connectors;
using Cvoya.Spring.Core.AgentRuntimes;
using Cvoya.Spring.Core.CredentialHealth;
using Cvoya.Spring.Core.Directory;
using Cvoya.Spring.Core.State;
using Cvoya.Spring.Host.Api.Auth;
using Cvoya.Spring.Host.Api.Models;

using Microsoft.AspNetCore.Mvc;

/// <summary>
/// Maps the generic, connector-agnostic surface under
/// <c>/api/v1/connectors</c> and <c>/api/v1/units/{id}/connector</c>. Every
/// connector-specific route (per-unit config, actions, config-schema) lives
/// on the connector package's own <see cref="IConnectorType.MapRoutes"/>
/// hook, so this file stays zero-knowledge of any individual connector.
/// </summary>
public static class ConnectorEndpoints
{
    /// <summary>
    /// Registers the platform-scoped connector endpoints under
    /// <c>/api/v1/platform/connectors</c>. These are gated to
    /// <see cref="RolePolicies.PlatformOperator"/> and cover cross-tenant
    /// operations: provisioning (making a connector type available
    /// platform-wide) and deprovisioning (#1259 / C1.2c).
    /// </summary>
    public static void MapPlatformConnectorEndpoints(this IEndpointRouteBuilder app)
    {
        var platform = app.MapGroup("/api/v1/platform/connectors")
            .WithTags("Connectors")
            .RequireAuthorization(RolePolicies.PlatformOperator);

        // Provision: make a connector type available platform-wide. The
        // connector type must already be registered in DI (i.e. its package
        // is installed on this Spring Voyage deployment). Records a
        // platform-level provisioning record in the state store so operators
        // can audit which connector types have been explicitly provisioned.
        platform.MapPost("/{slug}/provision", ProvisionConnectorAsync)
            .WithName("ProvisionConnector")
            .WithSummary("Provision a connector type platform-wide (PlatformOperator only; idempotent)")
            .Produces<ProvisionedConnectorResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        // Deprovision: remove the platform-wide provisioning record. Does
        // not uninstall the connector from any tenant — each tenant's bind
        // row is separate. A deprovisioned connector type is no longer
        // eligible for new tenant binds.
        platform.MapDelete("/{slug}", DeprovisionConnectorAsync)
            .WithName("DeprovisionConnector")
            .WithSummary("Deprovision a connector type platform-wide (PlatformOperator only)")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);
    }

    /// <summary>
    /// Registers the generic connector endpoints and invokes each
    /// registered <see cref="IConnectorType"/>'s <c>MapRoutes</c> under a
    /// pre-scoped <c>/api/v1/connectors/{slug}</c> group.
    /// </summary>
    public static void MapConnectorEndpoints(this IEndpointRouteBuilder app)
    {
        var connectors = app.MapGroup("/api/v1/tenant/connectors")
            .WithTags("Connectors");

        // Tenant-scoped list/get (#714). The list/get endpoints no longer
        // surface every connector type registered with the host; they now
        // return only the connectors installed on the caller's tenant, to
        // match the agent-runtimes surface (#693). A connector must be
        // installed on the current tenant before the wizard, CLI, or unit
        // Connector tab can see it.
        connectors.MapGet("/", ListConnectorsAsync)
            .WithName("ListConnectors")
            .WithSummary("List every connector installed on the current tenant")
            .Produces<InstalledConnectorResponse[]>(StatusCodes.Status200OK)
            .RequireAuthorization();

        connectors.MapGet("/{slugOrId}", GetConnectorAsync)
            .WithName("GetConnector")
            .WithSummary("Get a single installed connector on the current tenant by slug or id")
            .Produces<InstalledConnectorResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .RequireAuthorization();

        connectors.MapGet("/{slugOrId}/bindings", ListConnectorBindingsAsync)
            .WithName("ListConnectorBindings")
            .WithSummary("List every unit bound to the given connector type (#520)")
            .Produces<ConnectorUnitBindingResponse[]>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound);

        // Tenant bind lifecycle (#1259 / C1.2c). `/bind` (POST) is the
        // tenant-scoped counterpart to the platform `/provision` verb —
        // a TenantOperator binds a provisioned connector to their tenant.
        // Renamed from `/install` in #1259 to clarify the authz split:
        // platform provisions, tenant binds. `DELETE /{slugOrId}` unbinds
        // (was: uninstalls) and `PATCH /{slugOrId}/config` replaces the
        // stored tenant-scoped config.
        connectors.MapPost("/{slugOrId}/bind", BindConnectorAsync)
            .WithName("BindConnector")
            .WithSummary("Bind (install) the connector on the current tenant (idempotent)")
            .Produces<InstalledConnectorResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .RequireAuthorization();

        connectors.MapDelete("/{slugOrId}", UnbindConnectorAsync)
            .WithName("UnbindConnector")
            .WithSummary("Unbind (uninstall) the connector from the current tenant")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .RequireAuthorization();

        connectors.MapPatch("/{slugOrId}/config", UpdateInstallConfigAsync)
            .WithName("UpdateConnectorInstallConfig")
            .WithSummary("Replace the tenant-scoped install configuration for a connector")
            .Produces<InstalledConnectorResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .RequireAuthorization();

        connectors.MapPost("/{slugOrId}/validate-credential", ValidateConnectorCredentialAsync)
            .WithName("ValidateConnectorCredential")
            .WithSummary("Validate a candidate credential against the connector's backing service; records the outcome in the credential-health store")
            .Produces<CredentialValidateResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .RequireAuthorization();

        connectors.MapGet("/{slugOrId}/credential-health", GetConnectorCredentialHealthAsync)
            .WithName("GetConnectorCredentialHealth")
            .WithSummary("Get the current credential-health row for a connector on the current tenant")
            .Produces<CredentialHealthResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .RequireAuthorization();

        // Each connector owns its typed routes under /api/v1/connectors/{slug}/...
        // The host calls MapRoutes on a pre-scoped group so the connector
        // package stays ignorant of the outer path structure.
        var types = app.ServiceProvider.GetServices<IConnectorType>().ToList();
        foreach (var type in types)
        {
            var slugGroup = app.MapGroup($"/api/v1/tenant/connectors/{type.Slug}")
                .RequireAuthorization();
            type.MapRoutes(slugGroup);
        }
    }

    /// <summary>
    /// Maps the unit-scoped connector pointer endpoints — <c>GET</c> and
    /// <c>DELETE</c> under <c>/api/v1/units/{id}/connector</c>. Called by
    /// <c>UnitEndpoints.MapUnitEndpoints</c> so the routes are tagged with
    /// the <c>Units</c> group. Lives here rather than in UnitEndpoints so
    /// UnitEndpoints stays free of any knowledge about connector types.
    /// </summary>
    public static void MapUnitConnectorPointerEndpoints(this RouteGroupBuilder unitsGroup)
    {
        unitsGroup.MapGet("/{id}/connector", GetUnitConnectorAsync)
            .WithName("GetUnitConnector")
            .WithSummary("Get a pointer to the unit's active connector binding, or 404 if unbound")
            .Produces<UnitConnectorPointerResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound);

        unitsGroup.MapDelete("/{id}/connector", ClearUnitConnectorAsync)
            .WithName("DeleteUnitConnector")
            .WithSummary("Unbind the unit from whatever connector it was wired to")
            .Produces(StatusCodes.Status204NoContent);
    }

    /// <summary>
    /// Handler for <c>GET /api/v1/connectors</c> (#714). Returns the
    /// connectors installed on the current tenant — a strict subset of
    /// the connectors registered with the host. Swap of semantics from
    /// the pre-#714 "every registered connector type" shape; the generic
    /// list was redundant with <see cref="ITenantConnectorInstallService"/>
    /// and made it too easy to surface tenants connectors they haven't
    /// installed.
    /// </summary>
    private static async Task<IResult> ListConnectorsAsync(
        [FromServices] ITenantConnectorInstallService installService,
        [FromServices] IEnumerable<IConnectorType> connectorTypes,
        CancellationToken cancellationToken)
    {
        var installs = await installService.ListAsync(cancellationToken);
        var typeIndex = connectorTypes.ToDictionary(
            c => c.Slug, StringComparer.OrdinalIgnoreCase);
        var rows = installs
            .Select(install => typeIndex.TryGetValue(install.ConnectorId, out var type)
                ? ToInstalledResponse(install, type)
                : null)
            .Where(r => r is not null)
            .Cast<InstalledConnectorResponse>()
            .ToArray();
        return Results.Ok(rows);
    }

    /// <summary>
    /// Handler for <c>GET /api/v1/connectors/{slugOrId}</c> (#714). Returns
    /// a 404 when the connector is not installed on the current tenant
    /// even if it is registered with the host.
    /// </summary>
    private static async Task<IResult> GetConnectorAsync(
        string slugOrId,
        [FromServices] ITenantConnectorInstallService installService,
        [FromServices] IEnumerable<IConnectorType> connectorTypes,
        CancellationToken cancellationToken)
    {
        var type = ResolveConnector(slugOrId, connectorTypes);
        if (type is null)
        {
            return Results.Problem(
                detail: $"Connector '{slugOrId}' is not registered.",
                statusCode: StatusCodes.Status404NotFound);
        }

        var install = await installService.GetAsync(type.Slug, cancellationToken);
        if (install is null)
        {
            return Results.Problem(
                detail: $"Connector '{type.Slug}' is not installed on the current tenant.",
                statusCode: StatusCodes.Status404NotFound);
        }
        return Results.Ok(ToInstalledResponse(install, type));
    }

    /// <summary>
    /// Handler for <c>GET /api/v1/connectors/{slugOrId}/bindings</c> (#520). Walks
    /// the unit directory the same way <c>UnitEndpoints.ListUnitsAsync</c> does
    /// (so any boundary/visibility filter that applies to the canonical unit
    /// list applies here too) and returns a binding row for every unit whose
    /// current connector binding matches the requested type. Replaces the
    /// portal's N+1 fan-out from #516.
    /// </summary>
    private static async Task<IResult> ListConnectorBindingsAsync(
        string slugOrId,
        [FromServices] IDirectoryService directoryService,
        [FromServices] IUnitConnectorConfigStore store,
        [FromServices] IEnumerable<IConnectorType> connectorTypes,
        CancellationToken cancellationToken)
    {
        var target = ResolveConnector(slugOrId, connectorTypes);
        if (target is null)
        {
            return Results.Problem(
                detail: $"Connector '{slugOrId}' is not registered.",
                statusCode: StatusCodes.Status404NotFound);
        }

        // Mirrors UnitEndpoints.ListUnitsAsync: whatever visibility filter
        // wraps the directory surface (today: none in OSS; tenant-scoped in
        // the cloud extension) applies transparently to this endpoint too,
        // so bindings never leak outside the caller's visible unit boundary.
        var entries = await directoryService.ListAllAsync(cancellationToken);
        var units = entries
            .Where(e => string.Equals(e.Address.Scheme, "unit", StringComparison.OrdinalIgnoreCase))
            .ToList();

        var rows = new List<ConnectorUnitBindingResponse>();
        foreach (var entry in units)
        {
            var unitId = entry.Address.Path;
            var binding = await store.GetAsync(unitId, cancellationToken);
            if (binding is null || binding.TypeId != target.TypeId)
            {
                continue;
            }
            rows.Add(new ConnectorUnitBindingResponse(
                UnitId: unitId,
                UnitName: unitId,
                UnitDisplayName: string.IsNullOrEmpty(entry.DisplayName) ? unitId : entry.DisplayName,
                TypeId: target.TypeId,
                TypeSlug: target.Slug,
                ConfigUrl: $"/api/v1/tenant/connectors/{target.Slug}/units/{unitId}/config",
                ActionsBaseUrl: $"/api/v1/tenant/connectors/{target.Slug}/actions"));
        }

        return Results.Ok(rows.ToArray());
    }

    private static async Task<IResult> GetUnitConnectorAsync(
        string id,
        [FromServices] IUnitConnectorConfigStore store,
        [FromServices] IEnumerable<IConnectorType> connectorTypes,
        CancellationToken cancellationToken)
    {
        var binding = await store.GetAsync(id, cancellationToken);
        if (binding is null)
        {
            return Results.Problem(
                detail: $"Unit '{id}' has no active connector binding.",
                statusCode: StatusCodes.Status404NotFound);
        }

        var connector = connectorTypes.FirstOrDefault(c => c.TypeId == binding.TypeId);
        // It's possible the binding references a type that is no longer
        // registered (e.g. the package was removed). Surface the stored ids
        // so operators can see the orphan rather than silently 404-ing.
        var slug = connector?.Slug ?? "unknown";
        return Results.Ok(new UnitConnectorPointerResponse(
            TypeId: binding.TypeId,
            TypeSlug: slug,
            ConfigUrl: $"/api/v1/tenant/connectors/{slug}/units/{id}/config",
            ActionsBaseUrl: $"/api/v1/tenant/connectors/{slug}/actions"));
    }

    private static async Task<IResult> ClearUnitConnectorAsync(
        string id,
        [FromServices] IUnitConnectorConfigStore store,
        [FromServices] IUnitConnectorRuntimeStore runtimeStore,
        CancellationToken cancellationToken)
    {
        await store.ClearAsync(id, cancellationToken);
        // Runtime metadata is cleared defensively — the actor store also
        // clears it on binding clear, but a cloud impl may split the two.
        await runtimeStore.ClearAsync(id, cancellationToken);
        return Results.NoContent();
    }

    // ---- Platform-level provision / deprovision handlers ----

    /// <summary>
    /// Key prefix for platform-provisioned connector records in the state store.
    /// </summary>
    private const string PlatformConnectorProvisionKeyPrefix = "platform:connector:provision:";

    /// <summary>
    /// State-store record persisted when a connector type is provisioned
    /// platform-wide. Stored under
    /// <c>platform:connector:provision:{slug}</c>.
    /// </summary>
    private sealed record ProvisionedConnectorRecord(
        string Slug,
        DateTimeOffset ProvisionedAt,
        DateTimeOffset UpdatedAt);

    private static async Task<IResult> ProvisionConnectorAsync(
        string slug,
        [FromServices] IEnumerable<IConnectorType> connectorTypes,
        [FromServices] IStateStore stateStore,
        CancellationToken cancellationToken)
    {
        var type = connectorTypes.FirstOrDefault(
            c => string.Equals(c.Slug, slug, StringComparison.OrdinalIgnoreCase));
        if (type is null)
        {
            return Results.Problem(
                detail: $"Connector '{slug}' is not registered with the host. Only connectors " +
                        "registered via DI (i.e. whose package is installed) can be provisioned.",
                statusCode: StatusCodes.Status404NotFound);
        }

        var key = PlatformConnectorProvisionKeyPrefix + type.Slug;
        var existing = await stateStore.GetAsync<ProvisionedConnectorRecord>(key, cancellationToken);
        var now = DateTimeOffset.UtcNow;
        var record = existing is null
            ? new ProvisionedConnectorRecord(type.Slug, now, now)
            : existing with { UpdatedAt = now };
        await stateStore.SetAsync(key, record, cancellationToken);

        return Results.Ok(new ProvisionedConnectorResponse(
            TypeId: type.TypeId,
            TypeSlug: type.Slug,
            DisplayName: type.DisplayName,
            Description: type.Description,
            ProvisionedAt: record.ProvisionedAt,
            UpdatedAt: record.UpdatedAt));
    }

    private static async Task<IResult> DeprovisionConnectorAsync(
        string slug,
        [FromServices] IEnumerable<IConnectorType> connectorTypes,
        [FromServices] IStateStore stateStore,
        CancellationToken cancellationToken)
    {
        var type = connectorTypes.FirstOrDefault(
            c => string.Equals(c.Slug, slug, StringComparison.OrdinalIgnoreCase));
        if (type is null)
        {
            return Results.Problem(
                detail: $"Connector '{slug}' is not registered with the host.",
                statusCode: StatusCodes.Status404NotFound);
        }

        var key = PlatformConnectorProvisionKeyPrefix + type.Slug;
        await stateStore.DeleteAsync(key, cancellationToken);
        return Results.NoContent();
    }

    // ---- Shared resolver ----

    private static IConnectorType? ResolveConnector(
        string slugOrId, IEnumerable<IConnectorType> connectorTypes)
    {
        if (Guid.TryParse(slugOrId, out var id))
        {
            var byId = connectorTypes.FirstOrDefault(c => c.TypeId == id);
            if (byId is not null)
            {
                return byId;
            }
        }
        return connectorTypes.FirstOrDefault(
            c => string.Equals(c.Slug, slugOrId, StringComparison.OrdinalIgnoreCase));
    }

    // ---- Tenant-level bind / unbind handlers ----

    private static async Task<IResult> BindConnectorAsync(
        string slugOrId,
        [FromBody] ConnectorInstallRequest? body,
        [FromServices] ITenantConnectorInstallService installService,
        [FromServices] IEnumerable<IConnectorType> connectorTypes,
        CancellationToken cancellationToken)
    {
        var type = ResolveConnector(slugOrId, connectorTypes);
        if (type is null)
        {
            return Results.Problem(
                detail: $"Connector '{slugOrId}' is not registered.",
                statusCode: StatusCodes.Status404NotFound);
        }

        var config = body is null ? null : new ConnectorInstallConfig(body.Config);
        var install = await installService.InstallAsync(type.Slug, config, cancellationToken);
        return Results.Ok(ToInstalledResponse(install, type));
    }

    private static async Task<IResult> UnbindConnectorAsync(
        string slugOrId,
        [FromServices] ITenantConnectorInstallService installService,
        [FromServices] IEnumerable<IConnectorType> connectorTypes,
        CancellationToken cancellationToken)
    {
        var type = ResolveConnector(slugOrId, connectorTypes);
        if (type is null)
        {
            // The resolver treats unknown slugs as 404 on every other route
            // in this file; surface the same contract for unbind so a
            // typo cannot silently succeed.
            return Results.Problem(
                detail: $"Connector '{slugOrId}' is not registered.",
                statusCode: StatusCodes.Status404NotFound);
        }
        await installService.UninstallAsync(type.Slug, cancellationToken);
        return Results.NoContent();
    }

    private static async Task<IResult> UpdateInstallConfigAsync(
        string slugOrId,
        [FromBody] ConnectorInstallConfig config,
        [FromServices] ITenantConnectorInstallService installService,
        [FromServices] IEnumerable<IConnectorType> connectorTypes,
        CancellationToken cancellationToken)
    {
        var type = ResolveConnector(slugOrId, connectorTypes);
        if (type is null)
        {
            return Results.Problem(
                detail: $"Connector '{slugOrId}' is not registered.",
                statusCode: StatusCodes.Status404NotFound);
        }
        try
        {
            var install = await installService.UpdateConfigAsync(type.Slug, config, cancellationToken);
            return Results.Ok(ToInstalledResponse(install, type));
        }
        catch (InvalidOperationException ex)
        {
            return Results.Problem(
                detail: ex.Message,
                statusCode: StatusCodes.Status404NotFound);
        }
    }

    private static InstalledConnectorResponse ToInstalledResponse(
        InstalledConnector install,
        IConnectorType type)
        => new(
            TypeId: type.TypeId,
            TypeSlug: type.Slug,
            DisplayName: type.DisplayName,
            Description: type.Description,
            ConfigUrl: $"/api/v1/tenant/connectors/{type.Slug}/units/{{unitId}}/config",
            ActionsBaseUrl: $"/api/v1/tenant/connectors/{type.Slug}/actions",
            ConfigSchemaUrl: $"/api/v1/tenant/connectors/{type.Slug}/config-schema",
            InstalledAt: install.InstalledAt,
            UpdatedAt: install.UpdatedAt,
            Config: install.Config.Config);

    private static async Task<IResult> ValidateConnectorCredentialAsync(
        string slugOrId,
        [FromBody] CredentialValidateRequest body,
        [FromServices] IEnumerable<IConnectorType> connectorTypes,
        [FromServices] ICredentialHealthStore credentialHealthStore,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(body);

        var type = ResolveConnector(slugOrId, connectorTypes);
        if (type is null)
        {
            return Results.Problem(
                detail: $"Connector '{slugOrId}' is not registered.",
                statusCode: StatusCodes.Status404NotFound);
        }

        var secretName = string.IsNullOrWhiteSpace(body.SecretName) ? "default" : body.SecretName;
        var result = await type.ValidateCredentialAsync(body.Credential ?? string.Empty, cancellationToken);

        // Connectors that return null from ValidateCredentialAsync do not
        // carry auth — surface that as an Unknown health status without
        // trying to persist, and return a friendly "nothing to check" body
        // so callers can distinguish this from an actual validation pass.
        if (result is null)
        {
            return Results.Ok(new CredentialValidateResponse(
                Valid: false,
                Status: CredentialHealthStatus.Unknown,
                ErrorMessage: $"Connector '{type.Slug}' does not require credentials."));
        }

        var persistent = MapToHealth(result.Status);
        if (result.Status != CredentialValidationStatus.NetworkError)
        {
            await credentialHealthStore.RecordAsync(
                CredentialHealthKind.Connector,
                type.Slug,
                secretName,
                persistent,
                lastError: result.ErrorMessage,
                cancellationToken);
        }

        return Results.Ok(new CredentialValidateResponse(
            Valid: result.Valid,
            Status: persistent,
            ErrorMessage: result.ErrorMessage));
    }

    private static async Task<IResult> GetConnectorCredentialHealthAsync(
        string slugOrId,
        [FromServices] IEnumerable<IConnectorType> connectorTypes,
        [FromServices] ICredentialHealthStore credentialHealthStore,
        [FromQuery] string? secretName,
        CancellationToken cancellationToken)
    {
        var type = ResolveConnector(slugOrId, connectorTypes);
        if (type is null)
        {
            return Results.Problem(
                detail: $"Connector '{slugOrId}' is not registered.",
                statusCode: StatusCodes.Status404NotFound);
        }

        var resolvedSecret = string.IsNullOrWhiteSpace(secretName) ? "default" : secretName;
        var row = await credentialHealthStore.GetAsync(
            CredentialHealthKind.Connector, type.Slug, resolvedSecret, cancellationToken);
        if (row is null)
        {
            return Results.Problem(
                detail: $"No credential-health row recorded for connector '{type.Slug}' / '{resolvedSecret}'.",
                statusCode: StatusCodes.Status404NotFound);
        }

        return Results.Ok(new CredentialHealthResponse(
            SubjectId: row.SubjectId,
            SecretName: row.SecretName,
            Status: row.Status,
            LastError: row.LastError,
            LastChecked: row.LastChecked));
    }

    private static CredentialHealthStatus MapToHealth(CredentialValidationStatus status) => status switch
    {
        CredentialValidationStatus.Valid => CredentialHealthStatus.Valid,
        CredentialValidationStatus.Invalid => CredentialHealthStatus.Invalid,
        CredentialValidationStatus.NetworkError => CredentialHealthStatus.Unknown,
        _ => CredentialHealthStatus.Unknown,
    };
}