// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dispatcher;

using Cvoya.Spring.Core.Execution;

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;

/// <summary>
/// Endpoint map for the <c>/v1/probes</c> surface — probes that do not
/// target an existing container by id. Today there is one route, the
/// transient-container HTTP probe used to health-check distroless sidecars
/// (see <see cref="IContainerRuntime.ProbeHttpFromTransientContainerAsync"/>);
/// future container-less probes (TCP, ICMP) belong here too.
/// </summary>
/// <remarks>
/// Auth and error-shape conventions match <c>/v1/containers</c> /
/// <c>/v1/networks</c>: bearer-token required, request validation returns
/// <see cref="DispatcherErrorResponse"/>, the worker-side runtime collapses
/// every failure mode into a single boolean so the polling loop owns retry
/// semantics uniformly.
/// </remarks>
public static class ProbesEndpoints
{
    private static class EventIds
    {
        public static readonly EventId TransientProbeRequested =
            new(6020, nameof(TransientProbeRequested));
        public static readonly EventId TransientProbeRejected =
            new(6021, nameof(TransientProbeRejected));
    }

    /// <summary>
    /// Maps the <c>/v1/probes</c> endpoints onto the supplied route builder.
    /// </summary>
    public static IEndpointRouteBuilder MapProbeEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/v1/probes").RequireAuthorization();

        group.MapPost("/transient", TransientAsync);

        return endpoints;
    }

    /// <summary>
    /// <c>POST /v1/probes/transient</c> — spawn a throwaway probe container
    /// on the named bridge network and return whether the URL answered 2xx.
    /// Used by <see cref="IDaprSidecarManager"/> to wait for distroless
    /// daprd sidecars (no <c>wget</c> / <c>curl</c> in PATH) where the
    /// per-container exec probe at <c>POST /v1/containers/{id}/probe</c>
    /// is unusable.
    /// </summary>
    internal static async Task<IResult> TransientAsync(
        [FromBody] TransientProbeHttpRequest request,
        IContainerRuntime runtime,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        var logger = loggerFactory.CreateLogger("Cvoya.Spring.Dispatcher.Probes");

        if (string.IsNullOrWhiteSpace(request.ProbeImage))
        {
            logger.LogWarning(
                EventIds.TransientProbeRejected,
                "Rejected transient probe: probeImage is required");
            return Results.BadRequest(new DispatcherErrorResponse
            {
                Code = "probe_image_required",
                Message = "Field 'probeImage' is required.",
            });
        }

        if (string.IsNullOrWhiteSpace(request.Network))
        {
            logger.LogWarning(
                EventIds.TransientProbeRejected,
                "Rejected transient probe: network is required");
            return Results.BadRequest(new DispatcherErrorResponse
            {
                Code = "network_required",
                Message = "Field 'network' is required.",
            });
        }

        if (string.IsNullOrWhiteSpace(request.Url))
        {
            logger.LogWarning(
                EventIds.TransientProbeRejected,
                "Rejected transient probe: url is required");
            return Results.BadRequest(new DispatcherErrorResponse
            {
                Code = "url_required",
                Message = "Field 'url' is required.",
            });
        }

        logger.LogInformation(
            EventIds.TransientProbeRequested,
            "Transient probe image={ProbeImage} network={Network} url={Url}",
            request.ProbeImage, request.Network, request.Url);

        var healthy = await runtime.ProbeHttpFromTransientContainerAsync(
            request.ProbeImage, request.Network, request.Url, cancellationToken);

        return Results.Ok(new TransientProbeHttpResponse { Healthy = healthy });
    }
}