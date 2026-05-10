// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Workflows.Activities;

using Cvoya.Spring.Core.State;
using Cvoya.Spring.Dapr.Actors;

using global::Dapr.Workflow;

using Microsoft.Extensions.Logging;

/// <summary>
/// Flows memory state from an ephemeral-with-memory clone back to its parent
/// before the clone is destroyed. Copies every per-thread channel and the
/// initiative state.
/// </summary>
public class FlowMemoryToParentActivity(
    IStateStore stateStore,
    ILoggerFactory loggerFactory) : WorkflowActivity<CloningInput, bool>
{
    private readonly ILogger _logger = loggerFactory.CreateLogger<FlowMemoryToParentActivity>();

    /// <inheritdoc />
    public override async Task<bool> RunAsync(WorkflowActivityContext context, CloningInput input)
    {
        // #2076 / ADR-0030 §3 §44: per-thread channels keyed by
        // Agent:Channel:{ThreadId} replace the single Agent:ActiveThread
        // slot. Copy the channel index plus every per-thread channel
        // listed in it from clone to parent.
        var cloneIndexKey = $"{input.TargetAgentId}:{StateKeys.ChannelIndex}";
        var index = await stateStore.GetAsync<List<string>>(cloneIndexKey);
        if (index is { Count: > 0 })
        {
            var parentIndexKey = $"{input.SourceAgentId}:{StateKeys.ChannelIndex}";
            await stateStore.SetAsync(parentIndexKey, index);

            foreach (var threadId in index)
            {
                var cloneChannelKey = $"{input.TargetAgentId}:{StateKeys.ChannelPrefix}{threadId}";
                var channel = await stateStore.GetAsync<object>(cloneChannelKey);
                if (channel is not null)
                {
                    var parentChannelKey = $"{input.SourceAgentId}:{StateKeys.ChannelPrefix}{threadId}";
                    await stateStore.SetAsync(parentChannelKey, channel);
                }
            }
        }

        // Copy initiative state from clone to parent.
        var cloneInitiativeKey = $"{input.TargetAgentId}:{StateKeys.InitiativeState}";
        var initiativeState = await stateStore.GetAsync<object>(cloneInitiativeKey);
        if (initiativeState is not null)
        {
            var parentInitiativeKey = $"{input.SourceAgentId}:{StateKeys.InitiativeState}";
            await stateStore.SetAsync(parentInitiativeKey, initiativeState);
        }

        _logger.LogInformation(
            "Flowed memory state from clone {CloneId} back to parent {ParentId}",
            input.TargetAgentId, input.SourceAgentId);

        return true;
    }
}
