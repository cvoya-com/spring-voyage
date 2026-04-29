// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Tools;

using System.Text.Json;

using Cvoya.Spring.Core.Tools;
using Cvoya.Spring.Dapr.Actors;

using Microsoft.Extensions.Logging;

/// <summary>
/// Platform tool that saves checkpoint data for the current thread.
/// Stores arbitrary JSON data in the agent's state under a thread-specific key.
/// </summary>
public class CheckpointTool(
    ToolExecutionContextAccessor contextAccessor,
    ILoggerFactory loggerFactory) : IPlatformTool
{
    private static readonly JsonElement Schema = JsonSerializer.SerializeToElement(new
    {
        type = "object",
        properties = new
        {
            data = new
            {
                type = "object",
                description = "Arbitrary checkpoint data to save."
            }
        },
        required = new[] { "data" },
        additionalProperties = false
    });

    private readonly ILogger _logger = loggerFactory.CreateLogger<CheckpointTool>();

    /// <inheritdoc />
    public string Name => "checkpoint";

    /// <inheritdoc />
    public string Description => "Save progress on the current thread.";

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

        var threadId = executionContext.ThreadId
            ?? throw new InvalidOperationException("Cannot checkpoint without an active thread.");

        var data = parameters.GetProperty("data");

        var stateKey = $"{StateKeys.CheckpointPrefix}{threadId}";

        _logger.LogDebug("Checkpoint for agent {AgentPath}, thread {ThreadId}",
            executionContext.AgentAddress.Path, threadId);

        await executionContext.StateManager.SetStateAsync(stateKey, data, cancellationToken);

        return JsonSerializer.SerializeToElement(new { Success = true });
    }
}