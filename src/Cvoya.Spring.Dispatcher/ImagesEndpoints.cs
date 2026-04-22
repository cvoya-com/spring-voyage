// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dispatcher;

using Cvoya.Spring.Core.Execution;

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;

/// <summary>
/// Endpoint map for the <c>/v1/images</c> surface. Closes a latent gap that
/// existed before Stage 2 of #522: <c>DispatcherClientContainerRuntime</c>
/// already POSTed to <c>/v1/images/pull</c>, but the dispatcher never mapped
/// the route, so every <c>PullImageActivity</c> silently 404'd. The fix is
/// just "wire the route the client expects" — no behavior change for
/// callers, only an end of the silent failure.
/// </summary>
public static class ImagesEndpoints
{
    private static class EventIds
    {
        public static readonly EventId ImagePullRequested =
            new(6020, nameof(ImagePullRequested));
        public static readonly EventId ImagePullRejected =
            new(6021, nameof(ImagePullRejected));
        public static readonly EventId ImagePullTimedOut =
            new(6022, nameof(ImagePullTimedOut));
    }

    /// <summary>
    /// Default pull deadline used when the request does not carry an explicit
    /// <c>timeoutSeconds</c>. 10 minutes matches the worker's default for
    /// <c>PullImageActivity</c> and is wide enough for first-time pulls of
    /// the larger Spring agent images on cold registries.
    /// </summary>
    private static readonly TimeSpan DefaultPullTimeout = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Maps the <c>/v1/images</c> endpoints onto the supplied route builder.
    /// </summary>
    public static IEndpointRouteBuilder MapImageEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/v1/images").RequireAuthorization();

        group.MapPost("/pull", PullAsync);

        return endpoints;
    }

    /// <summary>
    /// <c>POST /v1/images/pull</c> — pull an image into the dispatcher's
    /// local image store. Returns 200 on success, 504 on timeout, and 502
    /// on any other runtime-reported pull failure. The client maps 504 to
    /// <see cref="System.TimeoutException"/> so <c>PullImageActivity</c>
    /// can keep its existing classification logic.
    /// </summary>
    internal static async Task<IResult> PullAsync(
        [FromBody] PullImageRequest request,
        IContainerRuntime runtime,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        var logger = loggerFactory.CreateLogger("Cvoya.Spring.Dispatcher.Images");

        if (string.IsNullOrWhiteSpace(request.Image))
        {
            logger.LogWarning(
                EventIds.ImagePullRejected,
                "Rejected image pull: image is required");
            return Results.BadRequest(new DispatcherErrorResponse
            {
                Code = "image_required",
                Message = "Field 'image' is required.",
            });
        }

        var timeout = request.TimeoutSeconds is { } seconds and > 0
            ? TimeSpan.FromSeconds(seconds)
            : DefaultPullTimeout;

        logger.LogInformation(
            EventIds.ImagePullRequested,
            "Pulling image {Image} (timeout={Timeout})", request.Image, timeout);

        try
        {
            await runtime.PullImageAsync(request.Image, timeout, cancellationToken);
            return Results.Ok();
        }
        catch (TimeoutException ex)
        {
            logger.LogWarning(
                EventIds.ImagePullTimedOut,
                ex,
                "Image pull timed out image={Image} timeout={Timeout}", request.Image, timeout);
            return Results.Json(
                new DispatcherErrorResponse
                {
                    Code = "image_pull_timeout",
                    Message = ex.Message,
                },
                statusCode: StatusCodes.Status504GatewayTimeout);
        }
        catch (InvalidOperationException ex)
        {
            // Distinct from 500 so the worker can tell "registry / runtime
            // refused the pull" apart from "the dispatcher itself crashed".
            // The client maps 502 back to InvalidOperationException with the
            // dispatcher's stderr text in the message, preserving the
            // signature PullImageActivity already classifies on.
            return Results.Json(
                new DispatcherErrorResponse
                {
                    Code = "image_pull_failed",
                    Message = ex.Message,
                },
                statusCode: StatusCodes.Status502BadGateway);
        }
    }
}