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
        public static readonly Microsoft.Extensions.Logging.EventId ContainerProbeRequested =
            new(6006, nameof(ContainerProbeRequested));
        public static readonly Microsoft.Extensions.Logging.EventId ContainerA2ARequested =
            new(6007, nameof(ContainerA2ARequested));
        public static readonly Microsoft.Extensions.Logging.EventId ContainerHealthRequested =
            new(6009, nameof(ContainerHealthRequested));
        public static readonly Microsoft.Extensions.Logging.EventId ContainerWaitForExitRequested =
            new(6010, nameof(ContainerWaitForExitRequested));
    }

    /// <summary>
    /// Maps the <c>/v1/containers</c> endpoints onto the supplied route builder.
    /// </summary>
    public static IEndpointRouteBuilder MapContainerEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/v1/containers").RequireAuthorization();

        group.MapPost("/", RunOrStartAsync);
        group.MapGet("/{id}/logs", GetLogsAsync);
        group.MapGet("/{id}/health", GetHealthAsync);
        group.MapPost("/{id}/probe", ProbeAsync);
        group.MapPost("/{id}/wait-for-exit", WaitForExitAsync);
        group.MapPost("/{id}/a2a", SendA2AAsync);
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

        // Per ADR-0055 the dispatcher no longer materialises workspace files —
        // the agent-sidecar pulls them from the worker bootstrap endpoint on
        // start. Mounts come straight from the request; the worker provisioned
        // the per-member workspace volume before issuing this call.
        var config = new ContainerConfig(
            Image: request.Image,
            // Prefer the new list-typed CommandArgs. Fall back to splitting
            // the legacy string field on whitespace for older clients —
            // this is intentionally lossy but matches the behaviour the
            // worker had before #1093 so the wire stays back-compat.
            Command: request.CommandArgs is { Count: > 0 } argv
                ? argv
                : (string.IsNullOrWhiteSpace(request.Command)
                    ? null
                    : request.Command.Split(' ', StringSplitOptions.RemoveEmptyEntries)),
            EnvironmentVariables: request.Env is null
                ? null
                : new Dictionary<string, string>(request.Env),
            VolumeMounts: request.Mounts,
            Timeout: request.TimeoutSeconds is { } ts ? TimeSpan.FromSeconds(ts) : null,
            NetworkName: request.NetworkName,
            AdditionalNetworks: request.AdditionalNetworks,
            Labels: request.Labels is null
                ? null
                : new Dictionary<string, string>(request.Labels),
            ExtraHosts: request.ExtraHosts,
            WorkingDirectory: request.WorkingDirectory,
            ContainerName: request.ContainerName,
            Entrypoint: request.Entrypoint);

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
    /// <c>GET /v1/containers/{id}/health</c> — read the native HEALTHCHECK
    /// status for a running container by inspecting the runtime's container
    /// metadata. Returns 200 when healthy, 503 when unhealthy, and 404 when
    /// no container is tracked under the supplied id.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This endpoint lets non-sidecar consumers (cloud overlay, monitoring,
    /// the <c>spring agent status</c> CLI) ask "is container X healthy?"
    /// without needing to share a network with the container or know whether
    /// it is a path-1 or path-3 agent. See issue #1079.
    /// </para>
    /// <para>
    /// The check calls <see cref="IContainerRuntime.GetHealthAsync"/> which
    /// shells out to <c>podman inspect --format '{{.State.Health.Status}}'</c>
    /// on the dispatcher host. No in-container tooling is required. Containers
    /// that declare no HEALTHCHECK instruction are reported as healthy
    /// (<c>method="inspect"</c>, <c>status="healthy"</c>,
    /// <c>reason="no healthcheck declared"</c>) by convention.
    /// </para>
    /// </remarks>
    internal static async Task<IResult> GetHealthAsync(
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
            EventIds.ContainerHealthRequested,
            "Fetching health for container id={ContainerId}", id);

        ContainerHealth health;
        try
        {
            health = await runtime.GetHealthAsync(id, cancellationToken);
        }
        catch (InvalidOperationException)
        {
            return Results.NotFound(new DispatcherErrorResponse
            {
                Code = "container_not_found",
                Message = $"Container '{id}' is not known to the dispatcher.",
            });
        }

        var checkedAt = DateTimeOffset.UtcNow;

        if (health.Healthy)
        {
            return Results.Ok(new ContainerHealthResponse
            {
                Status = "healthy",
                CheckedAt = checkedAt,
                Method = "inspect",
            });
        }

        return Results.Json(
            new ContainerHealthResponse
            {
                Status = "unhealthy",
                Reason = health.Detail,
                CheckedAt = checkedAt,
            },
            statusCode: StatusCodes.Status503ServiceUnavailable);
    }

    /// <summary>
    /// <c>POST /v1/containers/{id}/probe</c> — run a one-shot HTTP probe
    /// (<c>curl</c>) inside the named container's network namespace and
    /// return whether the URL answered 2xx. Used by the worker-side
    /// <c>DaprSidecarManager</c> to poll <c>/v1.0/healthz/outbound</c> on
    /// a paired daprd sidecar by exec'ing into the app container, and by
    /// agent-readiness call sites (<c>A2AExecutionDispatcher</c>,
    /// <c>PersistentAgentRegistry</c>, <c>ContainerSupervisorActor</c>) to
    /// probe <c>http://localhost:8999/.well-known/agent.json</c> inside the
    /// agent container itself. Per ADR 0028 Decision A, <c>podman exec</c>
    /// is the only mechanism the dispatcher uses to reach into a tenant
    /// container.
    /// </summary>
    internal static async Task<IResult> ProbeAsync(
        string id,
        [FromBody] ProbeContainerHttpRequest request,
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

        if (string.IsNullOrWhiteSpace(request.Url))
        {
            return Results.BadRequest(new DispatcherErrorResponse
            {
                Code = "url_required",
                Message = "Field 'url' is required.",
            });
        }

        logger.LogInformation(
            EventIds.ContainerProbeRequested,
            "Probing container id={ContainerId} url={Url}", id, request.Url);

        var healthy = await runtime.ProbeContainerHttpAsync(id, request.Url, cancellationToken);
        return Results.Ok(new ProbeContainerHttpResponse { Healthy = healthy });
    }

    /// <summary>
    /// <c>POST /v1/containers/{id}/wait-for-exit</c> — long-poll until the
    /// named (already-started) container exits, then return the exit code +
    /// captured stdout / stderr. Added in #2198 so
    /// <c>ContainerLifecycleManager</c> can decompose Run into Start +
    /// (probe daprd via exec into app) + Wait — necessary because daprd
    /// itself is distroless and has no curl/wget, so its readiness must be
    /// probed via exec into a peer container that does.
    /// </summary>
    internal static async Task<IResult> WaitForExitAsync(
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
            EventIds.ContainerWaitForExitRequested,
            "Waiting for container id={ContainerId} to exit", id);

        try
        {
            var result = await runtime.WaitForExitAsync(id, cancellationToken);
            return Results.Ok(new WaitForExitResponse
            {
                Id = result.ContainerId,
                ExitCode = result.ExitCode,
                StandardOutput = result.StandardOutput,
                StandardError = result.StandardError,
            });
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("no container", StringComparison.OrdinalIgnoreCase))
        {
            // ProcessContainerRuntime surfaces unknown-container as
            // InvalidOperationException with stderr embedded; map to 404.
            return Results.NotFound(new DispatcherErrorResponse
            {
                Code = "container_not_found",
                Message = ex.Message,
            });
        }
    }

    /// <summary>
    /// <c>POST /v1/containers/{id}/a2a</c> — forward a JSON HTTP <c>POST</c>
    /// into the named container's network namespace and return the response.
    /// Symmetric with <see cref="ProbeAsync"/>: the dispatcher executes the
    /// request from inside the container so it works when the worker process
    /// and the agent container live on different bridge networks (the
    /// message-send half of #1160). See
    /// <c>IContainerRuntime.SendHttpJsonAsync</c> for why the surface is
    /// deliberately narrow (POST + JSON body only).
    /// </summary>
    internal static async Task<IResult> SendA2AAsync(
        string id,
        [FromBody] SendContainerHttpJsonRequest request,
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

        if (string.IsNullOrWhiteSpace(request.Url))
        {
            return Results.BadRequest(new DispatcherErrorResponse
            {
                Code = "url_required",
                Message = "Field 'url' is required.",
            });
        }

        if (request.BodyBase64 is null)
        {
            return Results.BadRequest(new DispatcherErrorResponse
            {
                Code = "body_required",
                Message = "Field 'bodyBase64' is required (use an empty string for an empty body).",
            });
        }

        byte[] bodyBytes;
        try
        {
            bodyBytes = request.BodyBase64.Length == 0
                ? []
                : Convert.FromBase64String(request.BodyBase64);
        }
        catch (FormatException ex)
        {
            return Results.BadRequest(new DispatcherErrorResponse
            {
                Code = "body_invalid",
                Message = $"Field 'bodyBase64' is not valid base64: {ex.Message}",
            });
        }

        logger.LogInformation(
            EventIds.ContainerA2ARequested,
            "Forwarding A2A POST to container id={ContainerId} url={Url} bytes={Bytes}",
            id, request.Url, bodyBytes.Length);

        var response = await runtime.SendHttpJsonAsync(id, request.Url, bodyBytes, cancellationToken);

        return Results.Ok(new SendContainerHttpJsonResponse
        {
            StatusCode = response.StatusCode,
            BodyBase64 = response.Body.Length == 0 ? string.Empty : Convert.ToBase64String(response.Body),
        });
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
