// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Tools;

using System.Text.Json;

using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Core.Tools;
using Cvoya.Spring.Dapr.Routing;

using Microsoft.Extensions.Logging;

/// <summary>
/// Platform tool that escalates an issue to a human or unit for re-routing.
/// Sends an escalation message via the <see cref="MessageRouter"/>.
/// </summary>
public class EscalateTool(
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
                description = "The address scheme of the escalation target (e.g., 'human', 'unit')."
            },
            targetPath = new
            {
                type = "string",
                description = "The address path of the escalation target."
            },
            reason = new
            {
                type = "string",
                description = "The reason for escalation."
            }
        },
        required = new[] { "targetScheme", "targetPath", "reason" },
        additionalProperties = false
    });

    private readonly ILogger _logger = loggerFactory.CreateLogger<EscalateTool>();

    /// <inheritdoc />
    public string Name => "escalate";

    /// <inheritdoc />
    public string Description => "Raise an issue to a human or unit for re-routing.";

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
        var reason = parameters.GetProperty("reason").GetString()
            ?? throw new InvalidOperationException("The 'reason' parameter is required.");

        _logger.LogInformation("Agent {AgentPath} escalating to {TargetScheme}://{TargetPath}: {Reason}",
            executionContext.AgentAddress.Path, targetScheme, targetPath, reason);

        var payload = JsonSerializer.SerializeToElement(new
        {
            Type = "escalation",
            Reason = reason
        });

        var escalationMessage = new Message(
            Guid.NewGuid(),
            executionContext.AgentAddress,
            new Address(targetScheme, targetPath),
            MessageType.Domain,
            executionContext.ThreadId,
            payload,
            DateTimeOffset.UtcNow);

        var result = await messageRouter.RouteAsync(escalationMessage, cancellationToken);

        if (!result.IsSuccess)
        {
            return JsonSerializer.SerializeToElement(new
            {
                Success = false,
                Error = result.Error?.Message
            });
        }

        return JsonSerializer.SerializeToElement(new { Success = true });
    }
}