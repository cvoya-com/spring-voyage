// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dispatcher;

using Cvoya.Spring.Core.Execution;

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;

/// <summary>
/// Endpoint map for the <c>/v1/containers</c> surface the dispatcher exposes
/// to workers. All endpoints require authentication via
/// <see cref="BearerTokenAuthHandler"/>.
/// </summary>
public static class ContainersEndpoints
{
    /// <summary>Event ID range for dispatcher endpoint logging.</summary>
    private static class EventIds
    {
        public static readonly Microsoft.Extensions.Logging.EventId ContainerRunRequested =
            new(6001, nameof(ContainerRunRequested));
        public static readonly Microsoft.Extensions.Logging.EventId ContainerStartRequested =
            new(6002, nameof(ContainerStartRequested));
        public static readonly Microsoft.Extensions.Logging.EventId ContainerStopRequested =
            new(6003, nameof(ContainerStopRequested));
        public static readonly Microsoft.Extensions.Logging.EventId DispatcherRejected =
            new(6004, nameof(DispatcherRejected));
        public static readonly Microsoft.Extensions.Logging.EventId ContainerLogsRequested =
            new(6005, nameof(ContainerLogsRequested));
    }

    /// <summary>
    /// Maps the <c>/v1/containers</c> endpoints onto the supplied route builder.
    /// </summary>
    public static IEndpointRouteBuilder MapContainerEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/v1/containers").RequireAuthorization();

        group.MapPost("/", RunOrStartAsync);
        group.MapGet("/{id}/logs", GetLogsAsync);
        group.MapDelete("/{id}", StopAsync);

        return endpoints;
    }

    /// <summary>
    /// <c>POST /v1/containers</c> — run a container (blocking) or start a
    /// detached container. Detached vs. blocking is selected by the
    /// <see cref="RunContainerRequest.Detached"/> flag.
    /// </summary>
    internal static async Task<IResult> RunOrStartAsync(
        [FromBody] RunContainerRequest request,
        IContainerRuntime runtime,
        ILoggerFactory loggerFactory,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        var logger = loggerFactory.CreateLogger("Cvoya.Spring.Dispatcher.Containers");

        if (string.IsNullOrWhiteSpace(request.Image))
        {
            logger.LogWarning(
                EventIds.DispatcherRejected,
                "Rejected container run: image is required");
            return Results.BadRequest(new DispatcherErrorResponse
            {
                Code = "image_required",
                Message = "Field 'image' is required.",
            });
        }

        var config = new ContainerConfig(
            Image: request.Image,
            Command: request.Command,
            EnvironmentVariables: request.Env is null
                ? null
                : new Dictionary<string, string>(request.Env),
            VolumeMounts: request.Mounts,
            Timeout: request.TimeoutSeconds is { } ts ? TimeSpan.FromSeconds(ts) : null,
            NetworkName: request.NetworkName,
            Labels: request.Labels is null
                ? null
                : new Dictionary<string, string>(request.Labels),
            ExtraHosts: request.ExtraHosts,
            WorkingDirectory: request.WorkingDirectory);

        if (request.Detached)
        {
            logger.LogInformation(
                EventIds.ContainerStartRequested,
                "Starting detached container image={Image}", request.Image);

            var id = await runtime.StartAsync(config, cancellationToken);
            return Results.Ok(new RunContainerResponse { Id = id });
        }

        logger.LogInformation(
            EventIds.ContainerRunRequested,
            "Running container image={Image}", request.Image);

        var result = await runtime.RunAsync(config, cancellationToken);
        return Results.Ok(new RunContainerResponse
        {
            Id = result.ContainerId,
            ExitCode = result.ExitCode,
            StandardOutput = result.StandardOutput,
            StandardError = result.StandardError,
        });
    }

    /// <summary>
    /// <c>GET /v1/containers/{id}/logs</c> — fetch the tail of a running or
    /// recently-stopped container's combined stdout+stderr.
    /// </summary>
    internal static async Task<IResult> GetLogsAsync(
        string id,
        IContainerRuntime runtime,
        ILoggerFactory loggerFactory,
        [FromQuery] int? tail,
        CancellationToken cancellationToken)
    {
        var logger = loggerFactory.CreateLogger("Cvoya.Spring.Dispatcher.Containers");

        if (string.IsNullOrWhiteSpace(id))
        {
            return Results.BadRequest(new DispatcherErrorResponse
            {
                Code = "id_required",
                Message = "Container id is required.",
            });
        }

        var effectiveTail = tail is > 0 ? tail.Value : 200;
        logger.LogInformation(
            EventIds.ContainerLogsRequested,
            "Fetching logs id={ContainerId} tail={Tail}", id, effectiveTail);

        try
        {
            var logs = await runtime.GetLogsAsync(id, effectiveTail, cancellationToken);
            return Results.Text(logs, contentType: "text/plain");
        }
        catch (InvalidOperationException)
        {
            return Results.NotFound(new DispatcherErrorResponse
            {
                Code = "container_not_found",
                Message = $"Container '{id}' is not known to the dispatcher.",
            });
        }
    }

    /// <summary>
    /// <c>DELETE /v1/containers/{id}</c> — stop and remove a running container.
    /// </summary>
    internal static async Task<IResult> StopAsync(
        string id,
        IContainerRuntime runtime,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        var logger = loggerFactory.CreateLogger("Cvoya.Spring.Dispatcher.Containers");

        if (string.IsNullOrWhiteSpace(id))
        {
            return Results.BadRequest(new DispatcherErrorResponse
            {
                Code = "id_required",
                Message = "Container id is required.",
            });
        }

        logger.LogInformation(
            EventIds.ContainerStopRequested,
            "Stopping container id={ContainerId}", id);

        await runtime.StopAsync(id, cancellationToken);
        return Results.NoContent();
    }
}