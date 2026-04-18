// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Api.Endpoints;

using Cvoya.Spring.Connectors;
using Cvoya.Spring.Core.Directory;
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
    /// Registers the generic connector endpoints and invokes each
    /// registered <see cref="IConnectorType"/>'s <c>MapRoutes</c> under a
    /// pre-scoped <c>/api/v1/connectors/{slug}</c> group.
    /// </summary>
    public static void MapConnectorEndpoints(this IEndpointRouteBuilder app)
    {
        var connectors = app.MapGroup("/api/v1/connectors")
            .WithTags("Connectors");

        connectors.MapGet("/", ListConnectorsAsync)
            .WithName("ListConnectors")
            .WithSummary("List every connector type the server knows about")
            .Produces<ConnectorTypeResponse[]>(StatusCodes.Status200OK);

        connectors.MapGet("/{slugOrId}", GetConnectorAsync)
            .WithName("GetConnector")
            .WithSummary("Get a single connector type by slug or id")
            .Produces<ConnectorTypeResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound);

        connectors.MapGet("/{slugOrId}/bindings", ListConnectorBindingsAsync)
            .WithName("ListConnectorBindings")
            .WithSummary("List every unit bound to the given connector type (#520)")
            .Produces<ConnectorUnitBindingResponse[]>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound);

        // Each connector owns its typed routes under /api/v1/connectors/{slug}/...
        // The host calls MapRoutes on a pre-scoped group so the connector
        // package stays ignorant of the outer path structure.
        var types = app.ServiceProvider.GetServices<IConnectorType>().ToList();
        foreach (var type in types)
        {
            var slugGroup = app.MapGroup($"/api/v1/connectors/{type.Slug}")
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

    private static Task<IResult> ListConnectorsAsync(
        [FromServices] IEnumerable<IConnectorType> connectorTypes)
    {
        var response = connectorTypes.Select(ToConnectorResponse).ToArray();
        return Task.FromResult(Results.Ok(response));
    }

    private static Task<IResult> GetConnectorAsync(
        string slugOrId,
        [FromServices] IEnumerable<IConnectorType> connectorTypes)
    {
        var connector = ResolveConnector(slugOrId, connectorTypes);
        if (connector is null)
        {
            return Task.FromResult(Results.Problem(
                detail: $"Connector '{slugOrId}' is not registered.",
                statusCode: StatusCodes.Status404NotFound));
        }
        return Task.FromResult(Results.Ok(ToConnectorResponse(connector)));
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
                ConfigUrl: $"/api/v1/connectors/{target.Slug}/units/{unitId}/config",
                ActionsBaseUrl: $"/api/v1/connectors/{target.Slug}/actions"));
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
            ConfigUrl: $"/api/v1/connectors/{slug}/units/{id}/config",
            ActionsBaseUrl: $"/api/v1/connectors/{slug}/actions"));
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

    private static ConnectorTypeResponse ToConnectorResponse(IConnectorType type)
        => new(
            TypeId: type.TypeId,
            TypeSlug: type.Slug,
            DisplayName: type.DisplayName,
            Description: type.Description,
            ConfigUrl: $"/api/v1/connectors/{type.Slug}/units/{{unitId}}/config",
            ActionsBaseUrl: $"/api/v1/connectors/{type.Slug}/actions",
            ConfigSchemaUrl: $"/api/v1/connectors/{type.Slug}/config-schema");
}