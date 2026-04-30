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
        public static readonly Microsoft.Extensions.Logging.EventId ContainerProbeFromHostRequested =
            new(6008, nameof(ContainerProbeFromHostRequested));
    }

    /// <summary>
    /// Maps the <c>/v1/containers</c> endpoints onto the supplied route builder.
    /// </summary>
    public static IEndpointRouteBuilder MapContainerEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/v1/containers").RequireAuthorization();

        group.MapPost("/", RunOrStartAsync);
        group.MapGet("/{id}/logs", GetLogsAsync);
        group.MapPost("/{id}/probe", ProbeAsync);
        group.MapPost("/{id}/probe-from-host", ProbeFromHostAsync);
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
        IWorkspaceMaterializer workspaceMaterializer,
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

        // Materialise the workspace BEFORE building the config so the bind-mount
        // spec and effective working directory both reference the dispatcher-host
        // path the agent container will actually see (issue #1042).
        MaterializedWorkspace? materialized = null;
        if (request.Workspace is { } workspaceRequest)
        {
            try
            {
                materialized = await workspaceMaterializer.MaterializeAsync(
                    workspaceRequest, cancellationToken);
            }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
            {
                logger.LogWarning(
                    EventIds.DispatcherRejected,
                    ex,
                    "Rejected container run: workspace request is invalid");
                return Results.BadRequest(new DispatcherErrorResponse
                {
                    Code = "workspace_invalid",
                    Message = ex.Message,
                });
            }
        }

        // D3a: materialise the context workspace (/spring/context/) when the
        // request carries one. Lifecycle is identical to the main workspace —
        // cleanup on run completion or on DELETE for detached starts. Errors
        // from a bad context-workspace request must clean up any already-
        // materialised main workspace to avoid host directory leaks.
        MaterializedWorkspace? materializedContext = null;
        if (request.ContextWorkspace is { } contextWorkspaceRequest)
        {
            try
            {
                materializedContext = await workspaceMaterializer.MaterializeAsync(
                    contextWorkspaceRequest, cancellationToken);
            }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
            {
                // Clean up the main workspace if we already created it.
                if (materialized is not null)
                {
                    workspaceMaterializer.Cleanup(materialized);
                }

                logger.LogWarning(
                    EventIds.DispatcherRejected,
                    ex,
                    "Rejected container run: context workspace request is invalid");
                return Results.BadRequest(new DispatcherErrorResponse
                {
                    Code = "context_workspace_invalid",
                    Message = ex.Message,
                });
            }
        }

        var mounts = BuildEffectiveMounts(request.Mounts, materialized, materializedContext);
        // Only default the workdir to the materialised mount path when the
        // workspace actually contains files. Launchers like DaprAgentLauncher
        // bind-mount an empty workspace just to keep the launch shape uniform
        // — they ship images whose CMD is relative to a fixed image WORKDIR
        // (e.g. `python agent.py` from /app). Silently overriding workdir to
        // /workspace in that case makes the relative CMD lookup fail and the
        // container exits immediately with "No such file or directory". This
        // mirrors the worker-side policy in ContainerConfigBuilder.Build —
        // see the long comment there for the rationale (#1159).
        var workdir = request.WorkingDirectory
            ?? (materialized is not null
                && request.Workspace is { Files.Count: > 0 }
                    ? materialized.MountPath
                    : null);

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
            VolumeMounts: mounts,
            Timeout: request.TimeoutSeconds is { } ts ? TimeSpan.FromSeconds(ts) : null,
            NetworkName: request.NetworkName,
            AdditionalNetworks: request.AdditionalNetworks,
            Labels: request.Labels is null
                ? null
                : new Dictionary<string, string>(request.Labels),
            ExtraHosts: request.ExtraHosts,
            WorkingDirectory: workdir,
            ContainerName: request.ContainerName);

        if (request.Detached)
        {
            logger.LogInformation(
                EventIds.ContainerStartRequested,
                "Starting detached container image={Image}", request.Image);

            try
            {
                var id = await runtime.StartAsync(config, cancellationToken);
                if (materialized is not null)
                {
                    // Detached starts: defer cleanup until DELETE /v1/containers/{id}.
                    workspaceMaterializer.TrackForContainer(id, materialized);
                }
                if (materializedContext is not null)
                {
                    // D3a: also track the context workspace for deferred cleanup.
                    workspaceMaterializer.TrackForContainer(id, materializedContext);
                }
                return Results.Ok(new RunContainerResponse { Id = id });
            }
            catch
            {
                // The runtime never owned the workspaces, so a start failure means
                // we leak the host dirs unless we sweep them here.
                if (materialized is not null)
                {
                    workspaceMaterializer.Cleanup(materialized);
                }
                if (materializedContext is not null)
                {
                    workspaceMaterializer.Cleanup(materializedContext);
                }
                throw;
            }
        }

        logger.LogInformation(
            EventIds.ContainerRunRequested,
            "Running container image={Image}", request.Image);

        try
        {
            var result = await runtime.RunAsync(config, cancellationToken);
            return Results.Ok(new RunContainerResponse
            {
                Id = result.ContainerId,
                ExitCode = result.ExitCode,
                StandardOutput = result.StandardOutput,
                StandardError = result.StandardError,
            });
        }
        finally
        {
            // Blocking runs always clean up — success or failure. Cleanup is
            // logged by the materialiser so operators can audit the lifecycle.
            if (materialized is not null)
            {
                workspaceMaterializer.Cleanup(materialized);
            }
            // D3a: clean up context workspace on the same lifecycle as the main workspace.
            if (materializedContext is not null)
            {
                workspaceMaterializer.Cleanup(materializedContext);
            }
        }
    }

    private static IReadOnlyList<string>? BuildEffectiveMounts(
        IReadOnlyList<string>? requested,
        MaterializedWorkspace? materialized,
        MaterializedWorkspace? materializedContext = null)
    {
        if (materialized is null && materializedContext is null)
        {
            return requested;
        }

        var capacity = (requested?.Count ?? 0)
            + (materialized is not null ? 1 : 0)
            + (materializedContext is not null ? 1 : 0);
        var mounts = new List<string>(capacity);
        if (requested is { Count: > 0 })
        {
            mounts.AddRange(requested);
        }
        if (materialized is not null)
        {
            mounts.Add(materialized.MountSpec);
        }
        // D3a: append the /spring/context/ bind-mount after the main workspace.
        if (materializedContext is not null)
        {
            mounts.Add(materializedContext.MountSpec);
        }
        return mounts;
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
    /// <c>POST /v1/containers/{id}/probe</c> — run a one-shot HTTP probe
    /// (<c>wget --spider</c>) inside the named container's network
    /// namespace and return whether the URL answered 2xx. Used by the
    /// worker-side <c>DaprSidecarManager</c> to poll
    /// <c>/v1.0/healthz</c> on a sidecar without holding its own
    /// container CLI binding (Stage 2 of #522 / #1063).
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
    /// <c>POST /v1/containers/{id}/probe-from-host</c> — probe an HTTP
    /// endpoint from the dispatcher host process by resolving the container's
    /// host-visible IP and issuing a plain HTTP GET. Requires no binary
    /// (<c>wget</c>, <c>curl</c>) inside the workload image. Replaces the
    /// in-container <c>podman exec … wget --spider</c> probe for A2A
    /// readiness checks (issue #1175).
    /// </summary>
    internal static async Task<IResult> ProbeFromHostAsync(
        string id,
        [FromBody] ProbeFromHostRequest request,
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
            EventIds.ContainerProbeFromHostRequested,
            "Host probe for container id={ContainerId} url={Url}", id, request.Url);

        var healthy = await runtime.ProbeHttpFromHostAsync(id, request.Url, cancellationToken);
        return Results.Ok(new ProbeFromHostResponse { Healthy = healthy });
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
        IWorkspaceMaterializer workspaceMaterializer,
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

        try
        {
            await runtime.StopAsync(id, cancellationToken);
        }
        finally
        {
            // Detached starts deferred workspace cleanup to this call —
            // if no workspace was tracked for this id this is a no-op.
            workspaceMaterializer.CleanupForContainer(id);
        }
        return Results.NoContent();
    }
}