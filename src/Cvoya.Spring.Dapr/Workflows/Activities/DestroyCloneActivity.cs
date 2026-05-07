// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Workflows.Activities;

using Cvoya.Spring.Core.Directory;
using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Core.State;
using Cvoya.Spring.Dapr.Actors;

using global::Dapr.Workflow;

using Microsoft.Extensions.Logging;

/// <summary>
/// Destroys an ephemeral clone by unregistering it from the directory,
/// cleaning up its state, and removing it from the parent's children list.
/// </summary>
public class DestroyCloneActivity(
    IDirectoryService directoryService,
    IStateStore stateStore,
    ILoggerFactory loggerFactory) : WorkflowActivity<CloningInput, bool>
{
    private readonly ILogger _logger = loggerFactory.CreateLogger<DestroyCloneActivity>();

    /// <inheritdoc />
    public override async Task<bool> RunAsync(WorkflowActivityContext context, CloningInput input)
    {
        // Unregister from directory.
        var address = Address.For("agent", input.TargetAgentId);
        await directoryService.UnregisterAsync(address);

        // Clean up clone state.
        await stateStore.DeleteAsync($"{input.TargetAgentId}:{StateKeys.CloneIdentity}");
        await stateStore.DeleteAsync($"{input.TargetAgentId}:{StateKeys.AgentDefinition}");
        await stateStore.DeleteAsync($"{input.TargetAgentId}:{StateKeys.ActiveConversation}");
        await stateStore.DeleteAsync($"{input.TargetAgentId}:{StateKeys.InitiativeState}");

        // Remove from parent's children list.
        var childrenKey = $"{input.SourceAgentId}:{StateKeys.CloneChildren}";
        var children = await stateStore.GetAsync<List<string>>(childrenKey);
        if (children is not null)
        {
            children.Remove(input.TargetAgentId);
            await stateStore.SetAsync(childrenKey, children);
        }

        _logger.LogInformation("Destroyed clone {CloneId} of parent {ParentId}",
            input.TargetAgentId, input.SourceAgentId);

        return true;
    }
}