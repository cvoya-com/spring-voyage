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

        // ADR-0040 / #2048: Agent:Definition is no longer mirrored in the
        // state store. The clone reads its parent's definition from the
        // agent_definitions EF table on activation; there is nothing to
        // copy here.

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
    /// Copies the parent's per-thread channels and initiative state to
    /// the clone. Per #2076 / ADR-0030 §3 §44 the per-thread channel map
    /// (<c>Agent:Channel:{ThreadId}</c>) replaces the single
    /// <c>Agent:ActiveThread</c> slot.
    /// </summary>
    private async Task CopyMemoryStateAsync(string parentId, string cloneId)
    {
        // Copy every per-thread channel listed in the parent's channel
        // index, plus the index itself.
        var parentIndexKey = $"{parentId}:{StateKeys.ChannelIndex}";
        var index = await stateStore.GetAsync<List<string>>(parentIndexKey);
        if (index is { Count: > 0 })
        {
            var cloneIndexKey = $"{cloneId}:{StateKeys.ChannelIndex}";
            await stateStore.SetAsync(cloneIndexKey, index);

            foreach (var threadId in index)
            {
                var parentChannelKey = $"{parentId}:{StateKeys.ChannelPrefix}{threadId}";
                var channel = await stateStore.GetAsync<object>(parentChannelKey);
                if (channel is not null)
                {
                    var cloneChannelKey = $"{cloneId}:{StateKeys.ChannelPrefix}{threadId}";
                    await stateStore.SetAsync(cloneChannelKey, channel);
                }
            }
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
