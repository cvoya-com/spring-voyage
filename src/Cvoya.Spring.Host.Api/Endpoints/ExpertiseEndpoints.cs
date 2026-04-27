// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Api.Endpoints;

using Cvoya.Spring.Core.Capabilities;
using Cvoya.Spring.Core.Directory;
using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Dapr.Actors;
using Cvoya.Spring.Host.Api.Models;

using global::Dapr.Actors;
using global::Dapr.Actors.Client;

using Microsoft.AspNetCore.Mvc;

/// <summary>
/// Expertise directory endpoints (#412).
/// </summary>
/// <remarks>
/// Three surfaces:
/// <list type="bullet">
///   <item><description><c>GET/PUT /api/v1/agents/{id}/expertise</c> — per-agent profile.</description></item>
///   <item><description><c>GET/PUT /api/v1/units/{id}/expertise/own</c> — the unit's own (non-aggregated) profile.</description></item>
///   <item><description><c>GET /api/v1/units/{id}/expertise</c> — effective / recursive-aggregated profile.</description></item>
/// </list>
/// Every write invalidates the aggregator cache for every ancestor unit so
/// the next aggregate read recomputes from live actor state.
/// </remarks>
public static class ExpertiseEndpoints
{
    /// <summary>
    /// Maps the expertise endpoints. Called from <c>Program.cs</c> after the
    /// main agent / unit endpoint groups are registered.
    /// </summary>
    public static IEndpointRouteBuilder MapExpertiseEndpoints(this IEndpointRouteBuilder app)
    {
        // Agent expertise.
        var agents = app.MapGroup("/api/v1/tenant/agents/{id}/expertise")
            .WithTags("Expertise")
            .RequireAuthorization(Auth.RolePolicies.TenantUser);

        agents.MapGet("/", GetAgentExpertiseAsync)
            .WithName("GetAgentExpertise")
            .WithSummary("Get an agent's configured expertise domains")
            .Produces<ExpertiseResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound);

        agents.MapPut("/", SetAgentExpertiseAsync)
            .WithName("SetAgentExpertise")
            .WithSummary("Replace an agent's configured expertise domains")
            .Produces<ExpertiseResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound);

        // Unit own expertise (non-aggregated).
        var unitOwn = app.MapGroup("/api/v1/tenant/units/{id}/expertise/own")
            .WithTags("Expertise")
            .RequireAuthorization(Auth.RolePolicies.TenantUser);

        unitOwn.MapGet("/", GetUnitOwnExpertiseAsync)
            .WithName("GetUnitOwnExpertise")
            .WithSummary("Get a unit's own (non-aggregated) expertise domains")
            .Produces<ExpertiseResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound);

        unitOwn.MapPut("/", SetUnitOwnExpertiseAsync)
            .WithName("SetUnitOwnExpertise")
            .WithSummary("Replace a unit's own (non-aggregated) expertise domains")
            .Produces<ExpertiseResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound);

        // Unit aggregated expertise (recursive composition to leaves).
        var unitAgg = app.MapGroup("/api/v1/tenant/units/{id}/expertise")
            .WithTags("Expertise")
            .RequireAuthorization(Auth.RolePolicies.TenantUser);

        unitAgg.MapGet("/", GetUnitAggregatedExpertiseAsync)
            .WithName("GetUnitAggregatedExpertise")
            .WithSummary("Get the effective (recursive-aggregated) expertise of a unit")
            .WithDescription("Walks the unit's member graph to the leaves, returning every capability contributed by the unit or any descendant, annotated with the contributing origin and the path from this unit to it.")
            .Produces<AggregatedExpertiseResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        return app;
    }

    private static async Task<IResult> GetAgentExpertiseAsync(
        string id,
        [FromServices] IDirectoryService directoryService,
        [FromServices] IActorProxyFactory actorProxyFactory,
        CancellationToken cancellationToken)
    {
        var address = new Address("agent", id);
        var entry = await directoryService.ResolveAsync(address, cancellationToken);
        if (entry is null)
        {
            return Results.Problem(detail: $"Agent '{id}' not found", statusCode: StatusCodes.Status404NotFound);
        }

        var proxy = actorProxyFactory.CreateActorProxy<IAgentActor>(
            new ActorId(entry.ActorId), nameof(AgentActor));
        var domains = await proxy.GetExpertiseAsync(cancellationToken);

        return Results.Ok(new ExpertiseResponse(domains.Select(ToDto).ToList()));
    }

    private static async Task<IResult> SetAgentExpertiseAsync(
        string id,
        SetExpertiseRequest request,
        [FromServices] IDirectoryService directoryService,
        [FromServices] IActorProxyFactory actorProxyFactory,
        [FromServices] IExpertiseAggregator aggregator,
        CancellationToken cancellationToken)
    {
        if (request?.Domains is null)
        {
            return Results.Problem(
                detail: "Domains list is required (use [] to clear).",
                statusCode: StatusCodes.Status400BadRequest);
        }

        var address = new Address("agent", id);
        var entry = await directoryService.ResolveAsync(address, cancellationToken);
        if (entry is null)
        {
            return Results.Problem(detail: $"Agent '{id}' not found", statusCode: StatusCodes.Status404NotFound);
        }

        var proxy = actorProxyFactory.CreateActorProxy<IAgentActor>(
            new ActorId(entry.ActorId), nameof(AgentActor));

        var domains = request.Domains.Select(FromDto).ToArray();
        await proxy.SetExpertiseAsync(domains, cancellationToken);

        // An agent-level edit reshapes the effective expertise of every unit
        // the agent participates in (and their ancestors). The aggregator's
        // InvalidateAsync walks that chain.
        await aggregator.InvalidateAsync(address, cancellationToken);

        var updated = await proxy.GetExpertiseAsync(cancellationToken);
        return Results.Ok(new ExpertiseResponse(updated.Select(ToDto).ToList()));
    }

    private static async Task<IResult> GetUnitOwnExpertiseAsync(
        string id,
        [FromServices] IDirectoryService directoryService,
        [FromServices] IActorProxyFactory actorProxyFactory,
        CancellationToken cancellationToken)
    {
        var address = new Address("unit", id);
        var entry = await directoryService.ResolveAsync(address, cancellationToken);
        if (entry is null)
        {
            return Results.Problem(detail: $"Unit '{id}' not found", statusCode: StatusCodes.Status404NotFound);
        }

        var proxy = actorProxyFactory.CreateActorProxy<IUnitActor>(
            new ActorId(entry.ActorId), nameof(UnitActor));
        var domains = await proxy.GetOwnExpertiseAsync(cancellationToken);

        return Results.Ok(new ExpertiseResponse(domains.Select(ToDto).ToList()));
    }

    private static async Task<IResult> SetUnitOwnExpertiseAsync(
        string id,
        SetExpertiseRequest request,
        [FromServices] IDirectoryService directoryService,
        [FromServices] IActorProxyFactory actorProxyFactory,
        [FromServices] IExpertiseAggregator aggregator,
        CancellationToken cancellationToken)
    {
        if (request?.Domains is null)
        {
            return Results.Problem(
                detail: "Domains list is required (use [] to clear).",
                statusCode: StatusCodes.Status400BadRequest);
        }

        var address = new Address("unit", id);
        var entry = await directoryService.ResolveAsync(address, cancellationToken);
        if (entry is null)
        {
            return Results.Problem(detail: $"Unit '{id}' not found", statusCode: StatusCodes.Status404NotFound);
        }

        var proxy = actorProxyFactory.CreateActorProxy<IUnitActor>(
            new ActorId(entry.ActorId), nameof(UnitActor));

        var domains = request.Domains.Select(FromDto).ToArray();
        await proxy.SetOwnExpertiseAsync(domains, cancellationToken);

        await aggregator.InvalidateAsync(address, cancellationToken);

        var updated = await proxy.GetOwnExpertiseAsync(cancellationToken);
        return Results.Ok(new ExpertiseResponse(updated.Select(ToDto).ToList()));
    }

    private static async Task<IResult> GetUnitAggregatedExpertiseAsync(
        string id,
        [FromServices] IDirectoryService directoryService,
        [FromServices] IExpertiseAggregator aggregator,
        CancellationToken cancellationToken)
    {
        var address = new Address("unit", id);
        var entry = await directoryService.ResolveAsync(address, cancellationToken);
        if (entry is null)
        {
            return Results.Problem(detail: $"Unit '{id}' not found", statusCode: StatusCodes.Status404NotFound);
        }

        try
        {
            var aggregated = await aggregator.GetAsync(address, cancellationToken);
            return Results.Ok(ToResponse(aggregated));
        }
        catch (ExpertiseAggregationException ex)
        {
            // Cycle or over-depth — project as 409 Conflict so the UI can
            // show the offending path without needing a specialised body.
            return Results.Problem(
                title: "Aggregation failed",
                detail: ex.Message,
                statusCode: StatusCodes.Status409Conflict,
                extensions: new Dictionary<string, object?>
                {
                    ["unit"] = new AddressDto(ex.Unit.Scheme, ex.Unit.Path),
                    ["path"] = ex.Path.Select(a => new AddressDto(a.Scheme, a.Path)).ToList(),
                });
        }
    }

    private static ExpertiseDomainDto ToDto(ExpertiseDomain domain) =>
        new(domain.Name, domain.Description, domain.Level?.ToString().ToLowerInvariant());

    private static ExpertiseDomain FromDto(ExpertiseDomainDto dto)
    {
        ExpertiseLevel? level = null;
        if (!string.IsNullOrWhiteSpace(dto.Level)
            && Enum.TryParse<ExpertiseLevel>(dto.Level, ignoreCase: true, out var parsed))
        {
            level = parsed;
        }
        return new ExpertiseDomain(dto.Name, dto.Description, level);
    }

    private static AggregatedExpertiseResponse ToResponse(AggregatedExpertise aggregated) =>
        new(
            new AddressDto(aggregated.Unit.Scheme, aggregated.Unit.Path),
            aggregated.Entries.Select(e => new AggregatedExpertiseEntryDto(
                ToDto(e.Domain),
                new AddressDto(e.Origin.Scheme, e.Origin.Path),
                e.Path.Select(a => new AddressDto(a.Scheme, a.Path)).ToList())).ToList(),
            aggregated.Depth,
            aggregated.ComputedAt);
}