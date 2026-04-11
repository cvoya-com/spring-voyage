// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Workflows.Activities;

using Cvoya.Spring.Core.State;
using Cvoya.Spring.Dapr.Actors;

using global::Dapr.Workflow;

using Microsoft.Extensions.Logging;

/// <summary>
/// Flows memory state from an ephemeral-with-memory clone back to its parent
/// before the clone is destroyed. Copies active conversation and initiative state.
/// </summary>
public class FlowMemoryToParentActivity(
    IStateStore stateStore,
    ILoggerFactory loggerFactory) : WorkflowActivity<CloningInput, bool>
{
    private readonly ILogger _logger = loggerFactory.CreateLogger<FlowMemoryToParentActivity>();

    /// <inheritdoc />
    public override async Task<bool> RunAsync(WorkflowActivityContext context, CloningInput input)
    {
        // Copy active conversation state from clone to parent.
        var cloneActiveKey = $"{input.TargetAgentId}:{StateKeys.ActiveConversation}";
        var activeConversation = await stateStore.GetAsync<object>(cloneActiveKey);
        if (activeConversation is not null)
        {
            var parentActiveKey = $"{input.SourceAgentId}:{StateKeys.ActiveConversation}";
            await stateStore.SetAsync(parentActiveKey, activeConversation);
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