// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Worker.Endpoints;

using Cvoya.Spring.Core;
using Cvoya.Spring.Dapr.Execution;

using Microsoft.AspNetCore.Mvc;

/// <summary>
/// Internal persistent-agent execution endpoints (ADR-0052 / Wave 3 / #2618).
/// The execution host (<c>spring-worker</c>) owns the persistent-agent
/// containers; these routes wrap <see cref="PersistentAgentLifecycle"/> and
/// <see cref="PersistentAgentRegistry"/> so the HTTP front door
/// (<c>spring-api</c>) can delegate deploy / undeploy / scale /
/// deployment-status / logs over Dapr service invocation instead of
/// resolving the execution singletons in-process.
/// </summary>
/// <remarks>
/// These routes are mounted under <c>/internal/</c> and are reachable only
/// through the Dapr app channel — they are not part of the public OpenAPI
/// surface and are not exposed on the public ingress. The route id is the
/// agent's actor Guid in canonical 32-char no-dash hex.
/// </remarks>
public static class PersistentAgentExecutionEndpoints
{
    /// <summary>
    /// Maps the internal persistent-agent execution routes onto
    /// <paramref name="app"/>.
    /// </summary>
    public static IEndpointRouteBuilder MapPersistentAgentExecutionEndpoints(
        this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/internal/agents/{id}");

        group.MapPost("/deploy", DeployAsync);
        group.MapPost("/undeploy", UndeployAsync);
        group.MapPost("/scale", ScaleAsync);
        group.MapGet("/deployment", GetDeploymentAsync);
        group.MapGet("/logs", GetLogsAsync);

        return app;
    }

    private static async Task<IResult> DeployAsync(
        string id,
        PersistentAgentDeployRequest? request,
        [FromServices] PersistentAgentLifecycle lifecycle,
        CancellationToken cancellationToken)
    {
        try
        {
            var entry = await lifecycle.DeployAsync(id, request?.ImageOverride, cancellationToken);
            return Results.Ok(ToState(entry));
        }
        catch (SpringException ex)
        {
            return Results.Problem(detail: ex.Message, statusCode: StatusCodes.Status400BadRequest);
        }
    }

    private static async Task<IResult> UndeployAsync(
        string id,
        [FromServices] PersistentAgentLifecycle lifecycle,
        CancellationToken cancellationToken)
    {
        await lifecycle.UndeployAsync(id, cancellationToken);
        return Results.Ok(PersistentAgentDeploymentState.NotRunning(id));
    }

    private static async Task<IResult> ScaleAsync(
        string id,
        PersistentAgentScaleRequest request,
        [FromServices] PersistentAgentLifecycle lifecycle,
        CancellationToken cancellationToken)
    {
        try
        {
            var entry = await lifecycle.ScaleAsync(id, request.Replicas, cancellationToken);
            return Results.Ok(ToState(entry));
        }
        catch (SpringException ex)
        {
            return Results.Problem(detail: ex.Message, statusCode: StatusCodes.Status400BadRequest);
        }
    }

    private static async Task<IResult> GetDeploymentAsync(
        string id,
        [FromServices] PersistentAgentRegistry registry,
        CancellationToken cancellationToken)
    {
        var entry = await registry.TryGetAsync(id, cancellationToken);
        return Results.Ok(entry is null
            ? PersistentAgentDeploymentState.NotRunning(id)
            : ToState(entry));
    }

    private static async Task<IResult> GetLogsAsync(
        string id,
        [FromServices] PersistentAgentLifecycle lifecycle,
        [FromServices] PersistentAgentRegistry registry,
        [FromQuery] int? tail,
        CancellationToken cancellationToken)
    {
        var effectiveTail = tail is > 0 ? tail.Value : 200;

        try
        {
            var logs = await lifecycle.GetLogsAsync(id, effectiveTail, cancellationToken);
            var registered = await registry.TryGetAsync(id, cancellationToken);
            return Results.Ok(new PersistentAgentLogsState(
                AgentId: id,
                ContainerId: registered?.ContainerId ?? string.Empty,
                Tail: effectiveTail,
                Logs: logs));
        }
        catch (SpringException ex)
        {
            // The lifecycle service throws when there is no tracked
            // deployment; the API host translates a 404 here into the
            // operator-facing "no deployment" message.
            return Results.Problem(detail: ex.Message, statusCode: StatusCodes.Status404NotFound);
        }
    }

    /// <summary>
    /// Projects a registry <see cref="PersistentAgentEntry"/> onto the
    /// internal-API <see cref="PersistentAgentDeploymentState"/>.
    /// </summary>
    private static PersistentAgentDeploymentState ToState(PersistentAgentEntry entry) =>
        new(
            AgentId: entry.AgentId,
            Running: entry.ContainerId is not null,
            HealthStatus: entry.HealthStatus switch
            {
                AgentHealthStatus.Healthy => "healthy",
                AgentHealthStatus.Unhealthy => "unhealthy",
                _ => "unknown",
            },
            // #2468: prefer the cross-process Image column on the entry over
            // the locally-cached AgentDefinition; the Definition slot is null
            // for entries rehydrated from the shared EF row.
            Image: entry.Image ?? entry.Definition?.Execution?.Image,
            Endpoint: entry.Endpoint?.ToString(),
            ContainerId: entry.ContainerId,
            StartedAt: entry.StartedAt,
            ConsecutiveFailures: entry.ConsecutiveFailures);
}
