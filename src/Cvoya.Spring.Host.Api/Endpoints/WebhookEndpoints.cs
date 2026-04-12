// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Api.Endpoints;

using System.Text;
using System.Text.Json;

using Cvoya.Spring.Connector.GitHub;
using Cvoya.Spring.Connector.GitHub.Auth;
using Cvoya.Spring.Connector.GitHub.Webhooks;
using Cvoya.Spring.Dapr.Routing;

using Microsoft.AspNetCore.Mvc;

/// <summary>
/// Maps inbound webhook endpoints for third-party integrations such as GitHub.
/// </summary>
public static class WebhookEndpoints
{
    private const string EventHeader = "X-GitHub-Event";
    private const string SignatureHeader = "X-Hub-Signature-256";
    private const string DeliveryHeader = "X-GitHub-Delivery";

    /// <summary>
    /// Registers webhook endpoints on the specified endpoint route builder.
    /// Webhook endpoints authenticate inbound requests via HMAC signature validation,
    /// not via the standard API authentication pipeline.
    /// </summary>
    /// <param name="app">The endpoint route builder.</param>
    /// <returns>The route group builder for chaining.</returns>
    public static RouteGroupBuilder MapWebhookEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/webhooks")
            .WithTags("Webhooks");

        group.MapPost("/github", HandleGitHubWebhookAsync)
            .WithName("HandleGitHubWebhook")
            .WithSummary("Receive a GitHub webhook event");

        return group;
    }

    private static async Task<IResult> HandleGitHubWebhookAsync(
        HttpContext httpContext,
        [FromServices] GitHubConnector githubConnector,
        [FromServices] GitHubConnectorOptions githubOptions,
        [FromServices] MessageRouter messageRouter,
        [FromServices] ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        var logger = loggerFactory.CreateLogger("Cvoya.Spring.Host.Api.Endpoints.WebhookEndpoints");

        var eventType = httpContext.Request.Headers[EventHeader].ToString();
        var signature = httpContext.Request.Headers[SignatureHeader].ToString();
        var deliveryId = httpContext.Request.Headers[DeliveryHeader].ToString();

        // Header values are attacker-controlled. Sanitize them before writing to logs so a
        // crafted value cannot forge fake log entries (CR/LF/control characters) or flood
        // the log stream. The raw value is still passed to the connector for routing.
        var safeEventType = SanitizeForLog(eventType);
        var safeDeliveryId = SanitizeForLog(deliveryId);

        if (string.IsNullOrEmpty(eventType))
        {
            logger.LogWarning("Rejected GitHub webhook: missing {Header} header (delivery {DeliveryId}).",
                EventHeader, safeDeliveryId);
            return Results.Problem(
                detail: $"Missing required header: {EventHeader}.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        if (string.IsNullOrEmpty(signature))
        {
            logger.LogWarning("Rejected GitHub webhook: missing {Header} header for event {EventType} (delivery {DeliveryId}).",
                SignatureHeader, safeEventType, safeDeliveryId);
            return Results.Problem(
                detail: $"Missing required header: {SignatureHeader}.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        string payload;
        try
        {
            httpContext.Request.EnableBuffering();
            using var reader = new StreamReader(
                httpContext.Request.Body,
                Encoding.UTF8,
                detectEncodingFromByteOrderMarks: false,
                leaveOpen: true);
            payload = await reader.ReadToEndAsync(cancellationToken);
            httpContext.Request.Body.Position = 0;
        }
        catch (Exception ex)
        {
            logger.LogError(ex,
                "Failed to read GitHub webhook body for event {EventType} (delivery {DeliveryId}).",
                safeEventType,
                safeDeliveryId);
            return Results.Problem(
                detail: "Failed to read webhook body.",
                statusCode: StatusCodes.Status500InternalServerError);
        }

        if (!WebhookSignatureValidator.Validate(payload, signature, githubOptions.WebhookSecret))
        {
            logger.LogWarning(
                "Rejected GitHub webhook: invalid signature for event {EventType} (delivery {DeliveryId}).",
                safeEventType,
                safeDeliveryId);
            return Results.Problem(
                detail: "Invalid webhook signature.",
                statusCode: StatusCodes.Status401Unauthorized);
        }

        try
        {
            using var document = JsonDocument.Parse(payload);
            var message = githubConnector.WebhookHandler.TranslateEvent(eventType, document.RootElement);

            if (message is null)
            {
                logger.LogInformation(
                    "GitHub webhook event {EventType} (delivery {DeliveryId}) did not produce a message; ignoring.",
                    safeEventType,
                    safeDeliveryId);
                return Results.Accepted();
            }

            var routeResult = await messageRouter.RouteAsync(message, cancellationToken);

            if (!routeResult.IsSuccess)
            {
                // Routing failure is a platform-level issue, not a GitHub retry signal.
                // Log and acknowledge so GitHub does not retry indefinitely.
                logger.LogWarning(
                    "GitHub webhook event {EventType} (delivery {DeliveryId}) produced a message but routing failed: {Error}",
                    safeEventType,
                    safeDeliveryId,
                    routeResult.Error?.Message ?? "unknown error");
            }

            return Results.Accepted();
        }
        catch (Exception ex)
        {
            logger.LogError(ex,
                "Unhandled error processing GitHub webhook event {EventType} (delivery {DeliveryId}).",
                safeEventType,
                safeDeliveryId);
            return Results.Problem(
                detail: "Unhandled error processing webhook.",
                statusCode: StatusCodes.Status500InternalServerError);
        }
    }

    /// <summary>
    /// Strips CR/LF and other control characters from attacker-controlled values before
    /// they reach the log stream, and caps the length so a crafted header cannot flood
    /// logs. Returns "unknown" for null/empty input so log messages remain readable.
    /// </summary>
    private static string SanitizeForLog(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return "unknown";
        }

        const int MaxLogValueLength = 128;
        var length = Math.Min(value.Length, MaxLogValueLength);
        var builder = new StringBuilder(length);
        for (var i = 0; i < length; i++)
        {
            var c = value[i];
            builder.Append(char.IsControl(c) ? '_' : c);
        }

        return builder.ToString();
    }
}