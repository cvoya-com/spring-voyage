// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dispatcher;

using Cvoya.Spring.Core.Execution;

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;

/// <summary>
/// Endpoint map for the <c>/v1/networks</c> surface. Carries
/// <c>ContainerLifecycleManager</c>'s network create/remove operations so
/// the worker container itself never holds a podman/docker binding.
/// </summary>
/// <remarks>
/// Every route requires the same bearer-token auth as <c>/v1/containers</c>.
/// Both routes are idempotent: repeated <c>POST</c> with the same name and
/// repeated <c>DELETE</c> of a missing network both return success so
/// <c>ContainerLifecycleManager</c>'s teardown path is safe after a partial
/// boot (Stage 2 of #522 / #1063).
/// </remarks>
public static class NetworksEndpoints
{
    private static class EventIds
    {
        public static readonly EventId NetworkCreateRequested =
            new(6010, nameof(NetworkCreateRequested));
        public static readonly EventId NetworkRemoveRequested =
            new(6011, nameof(NetworkRemoveRequested));
        public static readonly EventId NetworkRejected =
            new(6012, nameof(NetworkRejected));
    }

    /// <summary>
    /// Maps the <c>/v1/networks</c> endpoints onto the supplied route builder.
    /// </summary>
    public static IEndpointRouteBuilder MapNetworkEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/v1/networks").RequireAuthorization();

        group.MapPost("/", CreateAsync);
        group.MapDelete("/{name}", RemoveAsync);

        return endpoints;
    }

    /// <summary>
    /// <c>POST /v1/networks</c> — create a container network. Idempotent: a
    /// second create with the same name returns 200, not 409.
    /// </summary>
    internal static async Task<IResult> CreateAsync(
        [FromBody] CreateNetworkRequest request,
        IContainerRuntime runtime,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        var logger = loggerFactory.CreateLogger("Cvoya.Spring.Dispatcher.Networks");

        if (string.IsNullOrWhiteSpace(request.Name))
        {
            logger.LogWarning(
                EventIds.NetworkRejected,
                "Rejected network create: name is required");
            return Results.BadRequest(new DispatcherErrorResponse
            {
                Code = "name_required",
                Message = "Field 'name' is required.",
            });
        }

        logger.LogInformation(
            EventIds.NetworkCreateRequested,
            "Creating network name={Name}", request.Name);

        await runtime.CreateNetworkAsync(request.Name, cancellationToken);
        return Results.Ok();
    }

    /// <summary>
    /// <c>DELETE /v1/networks/{name}</c> — remove a container network.
    /// Idempotent: removing a missing network returns 204, not 404.
    /// </summary>
    internal static async Task<IResult> RemoveAsync(
        string name,
        IContainerRuntime runtime,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        var logger = loggerFactory.CreateLogger("Cvoya.Spring.Dispatcher.Networks");

        if (string.IsNullOrWhiteSpace(name))
        {
            return Results.BadRequest(new DispatcherErrorResponse
            {
                Code = "name_required",
                Message = "Network name is required.",
            });
        }

        logger.LogInformation(
            EventIds.NetworkRemoveRequested,
            "Removing network name={Name}", name);

        await runtime.RemoveNetworkAsync(name, cancellationToken);
        return Results.NoContent();
    }
}