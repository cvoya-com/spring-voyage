// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Api.Endpoints;

using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Dapr.Routing;
using Cvoya.Spring.Host.Api.Models;

/// <summary>
/// Maps message-related API endpoints.
/// </summary>
public static class MessageEndpoints
{
    /// <summary>
    /// Registers message endpoints on the specified endpoint route builder.
    /// </summary>
    /// <param name="app">The endpoint route builder.</param>
    /// <returns>The route group builder for chaining.</returns>
    public static RouteGroupBuilder MapMessageEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/messages")
            .WithTags("Messages");

        group.MapPost("/", SendMessageAsync)
            .WithName("SendMessage")
            .WithSummary("Send a message routed via the message router");

        return group;
    }

    private static async Task<IResult> SendMessageAsync(
        SendMessageRequest request,
        MessageRouter messageRouter,
        IConfiguration configuration,
        CancellationToken cancellationToken)
    {
        var isLocalDev = configuration.GetValue<bool>("LocalDev");
        var from = isLocalDev
            ? new Address("human", "local/dev")
            : new Address("human", "api");

        if (!Enum.TryParse<MessageType>(request.Type, ignoreCase: true, out var messageType))
        {
            return Results.BadRequest(new { Error = $"Invalid message type: '{request.Type}'" });
        }

        var to = new Address(request.To.Scheme, request.To.Path);
        var messageId = Guid.NewGuid();

        var message = new Message(
            messageId,
            from,
            to,
            messageType,
            request.ConversationId,
            request.Payload,
            DateTimeOffset.UtcNow);

        var result = await messageRouter.RouteAsync(message, cancellationToken);

        if (!result.IsSuccess)
        {
            var error = result.Error!;
            var statusCode = error.Code switch
            {
                "ADDRESS_NOT_FOUND" => StatusCodes.Status404NotFound,
                "PERMISSION_DENIED" => StatusCodes.Status403Forbidden,
                _ => StatusCodes.Status502BadGateway
            };

            return Results.Problem(
                detail: error.Message,
                statusCode: statusCode);
        }

        return Results.Ok(new MessageResponse(messageId, result.Value?.Payload));
    }
}