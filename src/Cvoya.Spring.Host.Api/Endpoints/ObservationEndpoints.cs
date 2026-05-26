// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Api.Endpoints;

using Cvoya.Spring.Core.Observability;
using Cvoya.Spring.Core.Security;
using Cvoya.Spring.Host.Api.Models;

using Microsoft.AspNetCore.Mvc;

/// <summary>
/// Maps the tenant-wide observation endpoints introduced by #2786 / #2787.
/// These surface every conversation thread in the caller's tenant — including
/// threads the caller is not a participant of — so administrators, auditors,
/// and other holders of <see cref="Cvoya.Spring.Core.Security.PlatformRoles.TenantObserver"/>
/// can observe interactions across the whole tenant.
/// </summary>
/// <remarks>
/// <para>
/// The shape mirrors <see cref="ThreadEndpoints"/> on purpose: callers get the
/// same enriched DTOs (<see cref="ThreadSummaryResponse"/> /
/// <see cref="ThreadDetailResponse"/>), so the portal and CLI can render
/// observation results with the identical components used for the
/// participant-scoped views.
/// </para>
/// <para>
/// Observation is read-only. There is no message-send endpoint here — even
/// when the caller is a participant of an observed thread, sending requires
/// <see cref="Cvoya.Spring.Core.Security.PlatformRoles.TenantUser"/> and goes
/// through <see cref="ThreadEndpoints"/>.
/// </para>
/// </remarks>
public static class ObservationEndpoints
{
    /// <summary>
    /// Registers the observation endpoints on the supplied route builder.
    /// </summary>
    /// <param name="app">The endpoint route builder.</param>
    /// <returns>The route group for chaining.</returns>
    public static RouteGroupBuilder MapObservationEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/tenant/observation/threads")
            .WithTags("Observation");

        group.MapGet("/", ListObservedThreadsAsync)
            .WithName("ListObservedThreads")
            .WithSummary("List every conversation thread in the tenant (tenant-wide observation)")
            .Produces<IReadOnlyList<ThreadSummaryResponse>>(StatusCodes.Status200OK);

        group.MapGet("/{id}", GetObservedThreadAsync)
            .WithName("GetObservedThread")
            .WithSummary("Get a single observed thread (summary + ordered events) by id")
            .Produces<ThreadDetailResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound);

        return group;
    }

    private static async Task<IResult> ListObservedThreadsAsync(
        [AsParameters] ThreadListQuery query,
        IThreadQueryService queryService,
        IParticipantDisplayNameResolver resolver,
        CancellationToken cancellationToken)
    {
        // The observation endpoint deliberately does NOT inject a caller
        // participant filter — the privilege of holding TenantObserver is
        // precisely "see every thread in the tenant". The optional
        // unit/agent/participant query params remain available as
        // narrowing hints (e.g., observe one unit's traffic) without
        // changing the privilege boundary. Tenant scoping is applied
        // automatically by the EF query filter on SpringDbContext.
        var filters = new ThreadQueryFilters(
            Unit: query.Unit,
            Agent: query.Agent,
            Participant: query.Participant,
            Limit: query.Limit,
            Archived: query.Archived);

        var summaries = await queryService.ListAsync(filters, cancellationToken);
        var enriched = await ThreadEndpoints.EnrichSummariesAsync(summaries, resolver, cancellationToken);
        return Results.Ok(enriched);
    }

    private static async Task<IResult> GetObservedThreadAsync(
        string id,
        IThreadQueryService queryService,
        IParticipantDisplayNameResolver resolver,
        CancellationToken cancellationToken)
    {
        var detail = await queryService.GetAsync(id, cancellationToken);
        if (detail is null)
        {
            return Results.Problem(
                detail: $"Thread '{id}' not found",
                statusCode: StatusCodes.Status404NotFound);
        }

        var enriched = await ThreadEndpoints.EnrichDetailAsync(detail, resolver, cancellationToken);
        return Results.Ok(enriched);
    }
}
