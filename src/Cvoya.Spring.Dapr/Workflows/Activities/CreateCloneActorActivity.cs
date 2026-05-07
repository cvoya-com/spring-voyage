// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Workflows.Activities;

using Cvoya.Spring.Core.Cloning;
using Cvoya.Spring.Core.State;
using Cvoya.Spring.Dapr.Actors;

using global::Dapr.Workflow;

using Microsoft.Extensions.Logging;

/// <summary>
/// Creates a clone actor and optionally copies the parent's memory state
/// based on the <see cref="CloningPolicy"/>.
/// </summary>
public class CreateCloneActorActivity(
    IStateStore stateStore,
    ILoggerFactory loggerFactory) : WorkflowActivity<CloningInput, bool>
{
    private readonly ILogger _logger = loggerFactory.CreateLogger<CreateCloneActorActivity>();

    /// <inheritdoc />
    public override async Task<bool> RunAsync(WorkflowActivityContext context, CloningInput input)
    {
        var cloneIdentity = new CloneIdentity(
            ParentAgentId: input.SourceAgentId,
            CloneId: input.TargetAgentId,
            CloningPolicy: input.CloningPolicy,
            AttachmentMode: input.AttachmentMode);

        // Store clone identity in the clone's state.
        var cloneIdentityKey = $"{input.TargetAgentId}:{StateKeys.CloneIdentity}";
        await stateStore.SetAsync(cloneIdentityKey, cloneIdentity);

        // Copy parent agent definition to clone.
        var parentDefinitionKey = $"{input.SourceAgentId}:{StateKeys.AgentDefinition}";
        var parentDefinition = await stateStore.GetAsync<object>(parentDefinitionKey);
        if (parentDefinition is not null)
        {
            var cloneDefinitionKey = $"{input.TargetAgentId}:{StateKeys.AgentDefinition}";
            await stateStore.SetAsync(cloneDefinitionKey, parentDefinition);
        }

        // Copy memory state if the policy requires it.
        if (input.CloningPolicy == CloningPolicy.EphemeralWithMemory)
        {
            await CopyMemoryStateAsync(input.SourceAgentId, input.TargetAgentId);
        }

        // Register this clone in the parent's children list.
        var childrenKey = $"{input.SourceAgentId}:{StateKeys.CloneChildren}";
        var children = await stateStore.GetAsync<List<string>>(childrenKey) ?? [];
        children.Add(input.TargetAgentId);
        await stateStore.SetAsync(childrenKey, children);

        _logger.LogInformation(
            "Created clone actor {CloneId} from parent {ParentId} with policy {CloningPolicy}",
            input.TargetAgentId, input.SourceAgentId, input.CloningPolicy);

        return true;
    }

    /// <summary>
    /// Copies checkpoint and conversation state from the parent to the clone.
    /// </summary>
    private async Task CopyMemoryStateAsync(string parentId, string cloneId)
    {
        // Copy active conversation state.
        var parentActiveKey = $"{parentId}:{StateKeys.ActiveConversation}";
        var activeThread = await stateStore.GetAsync<object>(parentActiveKey);
        if (activeThread is not null)
        {
            var cloneActiveKey = $"{cloneId}:{StateKeys.ActiveConversation}";
            await stateStore.SetAsync(cloneActiveKey, activeThread);
        }

        // Copy initiative state.
        var parentInitiativeKey = $"{parentId}:{StateKeys.InitiativeState}";
        var initiativeState = await stateStore.GetAsync<object>(parentInitiativeKey);
        if (initiativeState is not null)
        {
            var cloneInitiativeKey = $"{cloneId}:{StateKeys.InitiativeState}";
            await stateStore.SetAsync(cloneInitiativeKey, initiativeState);
        }

        _logger.LogInformation("Copied memory state from parent {ParentId} to clone {CloneId}",
            parentId, cloneId);
    }
}