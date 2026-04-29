// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Tools;

using System.Text.Json;

using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Core.Tools;
using Cvoya.Spring.Dapr.Routing;

using Microsoft.Extensions.Logging;

/// <summary>
/// Platform tool that sends a domain message to another agent for assistance.
/// Uses the <see cref="MessageRouter"/> to deliver the message and returns the response.
/// </summary>
public class RequestHelpTool(
    MessageRouter messageRouter,
    ToolExecutionContextAccessor contextAccessor,
    ILoggerFactory loggerFactory) : IPlatformTool
{
    private static readonly JsonElement Schema = JsonSerializer.SerializeToElement(new
    {
        type = "object",
        properties = new
        {
            targetScheme = new
            {
                type = "string",
                description = "The address scheme of the target (e.g., 'agent')."
            },
            targetPath = new
            {
                type = "string",
                description = "The address path of the target (e.g., 'engineering-team/ada')."
            },
            message = new
            {
                type = "string",
                description = "The message content to send."
            }
        },
        required = new[] { "targetScheme", "targetPath", "message" },
        additionalProperties = false
    });

    private readonly ILogger _logger = loggerFactory.CreateLogger<RequestHelpTool>();

    /// <inheritdoc />
    public string Name => "requestHelp";

    /// <inheritdoc />
    public string Description => "Send a message to another agent for assistance.";

    /// <inheritdoc />
    public JsonElement ParametersSchema => Schema;

    /// <inheritdoc />
    public async Task<JsonElement> ExecuteAsync(
        JsonElement parameters,
        JsonElement context,
        CancellationToken cancellationToken = default)
    {
        var executionContext = contextAccessor.Current
            ?? throw new InvalidOperationException("Tool execution context is not set.");

        var targetScheme = parameters.GetProperty("targetScheme").GetString()
            ?? throw new InvalidOperationException("The 'targetScheme' parameter is required.");
        var targetPath = parameters.GetProperty("targetPath").GetString()
            ?? throw new InvalidOperationException("The 'targetPath' parameter is required.");
        var messageContent = parameters.GetProperty("message").GetString()
            ?? throw new InvalidOperationException("The 'message' parameter is required.");

        _logger.LogDebug("RequestHelp from {AgentPath} to {TargetScheme}://{TargetPath}",
            executionContext.AgentAddress.Path, targetScheme, targetPath);

        var payload = JsonSerializer.SerializeToElement(new { Content = messageContent });

        var domainMessage = new Message(
            Guid.NewGuid(),
            executionContext.AgentAddress,
            new Address(targetScheme, targetPath),
            MessageType.Domain,
            executionContext.ThreadId,
            payload,
            DateTimeOffset.UtcNow);

        var result = await messageRouter.RouteAsync(domainMessage, cancellationToken);

        if (!result.IsSuccess)
        {
            return JsonSerializer.SerializeToElement(new
            {
                Success = false,
                Error = result.Error?.Message
            });
        }

        if (result.Value is null)
        {
            return JsonSerializer.SerializeToElement(new { Success = true, Response = (object?)null });
        }

        return JsonSerializer.SerializeToElement(new
        {
            Success = true,
            Response = result.Value.Payload
        });
    }
}