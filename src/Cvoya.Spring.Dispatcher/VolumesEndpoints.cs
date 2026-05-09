// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dispatcher;

using Cvoya.Spring.Core.Execution;

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;

/// <summary>
/// Endpoint map for the <c>/v1/volumes</c> surface (D3c — ADR-0029). Carries
/// per-agent workspace volume create/remove/metrics operations so the worker
/// container never holds a podman/docker binding.
/// </summary>
/// <remarks>
/// <para>
/// Every route requires the same bearer-token auth as <c>/v1/containers</c>.
/// </para>
/// <para>
/// Both mutating routes are idempotent: repeated <c>POST</c> with the same name
/// and repeated <c>DELETE</c> of a missing volume both return success so
/// reclamation paths are safe after a partial failure.
/// </para>
/// </remarks>
public static class VolumesEndpoints
{
    private static class EventIds
    {
        public static readonly EventId VolumeCreateRequested =
            new(6020, nameof(VolumeCreateRequested));
        public static readonly EventId VolumeRemoveRequested =
            new(6021, nameof(VolumeRemoveRequested));
        public static readonly EventId VolumeMetricsRequested =
            new(6022, nameof(VolumeMetricsRequested));
        public static readonly EventId VolumeRejected =
            new(6023, nameof(VolumeRejected));
    }

    /// <summary>
    /// Maps the <c>/v1/volumes</c> endpoints onto the supplied route builder.
    /// </summary>
    public static IEndpointRouteBuilder MapVolumeEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/v1/volumes").RequireAuthorization();

        group.MapPost("/", CreateAsync);
        group.MapDelete("/{name}", RemoveAsync);
        group.MapGet("/{name}/metrics", GetMetricsAsync);

        return endpoints;
    }

    /// <summary>
    /// <c>POST /v1/volumes</c> — ensure a named volume exists, creating it if
    /// absent. Idempotent: a second create with the same name returns 200.
    /// </summary>
    internal static async Task<IResult> CreateAsync(
        [FromBody] CreateVolumeRequest request,
        IContainerRuntime runtime,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        var logger = loggerFactory.CreateLogger("Cvoya.Spring.Dispatcher.Volumes");

        if (string.IsNullOrWhiteSpace(request.Name))
        {
            logger.LogWarning(
                EventIds.VolumeRejected,
                "Rejected volume create: name is required");
            return Results.BadRequest(new DispatcherErrorResponse
            {
                Code = "name_required",
                Message = "Field 'name' is required.",
            });
        }

        logger.LogInformation(
            EventIds.VolumeCreateRequested,
            "Ensuring volume name={Name}", request.Name);

        await runtime.EnsureVolumeAsync(request.Name, cancellationToken);
        return Results.Ok();
    }

    /// <summary>
    /// <c>DELETE /v1/volumes/{name}</c> — remove a named volume. Idempotent:
    /// removing a missing volume returns 204, not 404.
    /// </summary>
    internal static async Task<IResult> RemoveAsync(
        string name,
        IContainerRuntime runtime,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        var logger = loggerFactory.CreateLogger("Cvoya.Spring.Dispatcher.Volumes");

        if (string.IsNullOrWhiteSpace(name))
        {
            return Results.BadRequest(new DispatcherErrorResponse
            {
                Code = "name_required",
                Message = "Volume name is required.",
            });
        }

        logger.LogInformation(
            EventIds.VolumeRemoveRequested,
            "Removing volume name={Name}", name);

        await runtime.RemoveVolumeAsync(name, cancellationToken);
        return Results.NoContent();
    }

    /// <summary>
    /// <c>GET /v1/volumes/{name}/metrics</c> — returns volume-level metrics
    /// (size, last-write) for a named volume. Returns 404 when the volume
    /// does not exist or the runtime cannot determine its size.
    /// </summary>
    internal static async Task<IResult> GetMetricsAsync(
        string name,
        IContainerRuntime runtime,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        var logger = loggerFactory.CreateLogger("Cvoya.Spring.Dispatcher.Volumes");

        if (string.IsNullOrWhiteSpace(name))
        {
            return Results.BadRequest(new DispatcherErrorResponse
            {
                Code = "name_required",
                Message = "Volume name is required.",
            });
        }

        logger.LogInformation(
            EventIds.VolumeMetricsRequested,
            "Querying metrics for volume name={Name}", name);

        var metrics = await runtime.GetVolumeMetricsAsync(name, cancellationToken);
        if (metrics is null)
        {
            return Results.NotFound();
        }

        return Results.Ok(new VolumeMetricsResponse
        {
            SizeBytes = metrics.SizeBytes,
            LastWrite = metrics.LastWrite,
        });
    }
}

/// <summary>
/// Wire shape for <c>POST /v1/volumes</c>.
/// </summary>
public record CreateVolumeRequest
{
    /// <summary>The named volume to create or ensure exists.</summary>
    public required string Name { get; init; }
}

/// <summary>
/// Wire shape returned by <c>GET /v1/volumes/{name}/metrics</c>.
/// </summary>
public record VolumeMetricsResponse
{
    /// <summary>Current volume disk usage in bytes, or null if unavailable.</summary>
    public long? SizeBytes { get; init; }

    /// <summary>Timestamp of the most recent write to the volume, or null if unavailable.</summary>
    public DateTimeOffset? LastWrite { get; init; }
}
