// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Tools;

using System.Text.Json;

using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Core.Tools;
using Cvoya.Spring.Dapr.Actors;

using Microsoft.Extensions.Logging;

/// <summary>
/// Platform tool that retrieves pending messages from the agent's active thread channel.
/// Returns the accumulated messages as a JSON array.
/// </summary>
public class CheckMessagesTool(
    ToolExecutionContextAccessor contextAccessor,
    ILoggerFactory loggerFactory) : IPlatformTool
{
    private static readonly JsonElement Schema = JsonSerializer.SerializeToElement(new
    {
        type = "object",
        properties = new { },
        additionalProperties = false
    });

    private readonly ILogger _logger = loggerFactory.CreateLogger<CheckMessagesTool>();

    /// <inheritdoc />
    public string Name => "checkMessages";

    /// <inheritdoc />
    public string Description => "Retrieve pending messages on the active thread.";

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

        _logger.LogDebug("CheckMessages for agent {AgentPath}, thread {ThreadId}",
            executionContext.AgentAddress.Path, executionContext.ThreadId);

        // #2076 / ADR-0030 §3 §44: per-thread channels, keyed by
        // Agent:Channel:{ThreadId}. The tool exposes pending messages for
        // the thread the dispatch is currently running on; threads other
        // than the executing one are not visible (and would not be useful
        // — the SDK exposes a peek_pending(thread_id) accessor for the
        // intended thread only).
        if (string.IsNullOrEmpty(executionContext.ThreadId))
        {
            return JsonSerializer.SerializeToElement(Array.Empty<object>());
        }

        var key = StateKeys.ChannelPrefix + executionContext.ThreadId;
        var channel = await executionContext.StateManager
            .TryGetStateAsync<ThreadChannel>(key, cancellationToken);

        if (!channel.HasValue || channel.Value.Messages.Count == 0)
        {
            return JsonSerializer.SerializeToElement(Array.Empty<object>());
        }

        return JsonSerializer.SerializeToElement(channel.Value.Messages);
    }
}
