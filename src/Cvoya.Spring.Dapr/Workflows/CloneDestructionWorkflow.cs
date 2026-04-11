// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Workflows;

using Cvoya.Spring.Core.Cloning;
using Cvoya.Spring.Dapr.Workflows.Activities;

using global::Dapr.Workflow;

/// <summary>
/// Dapr Workflow that orchestrates the destruction of an ephemeral agent clone.
/// For ephemeral-with-memory clones, memory is flowed back to the parent before cleanup.
/// </summary>
public class CloneDestructionWorkflow : Workflow<CloningInput, CloningOutput>
{
    /// <inheritdoc />
    public override async Task<CloningOutput> RunAsync(WorkflowContext context, CloningInput input)
    {
        // Step 1: For ephemeral-with-memory, flow memory back to parent.
        if (input.CloningPolicy == CloningPolicy.EphemeralWithMemory)
        {
            var memoryFlowed = await context.CallActivityAsync<bool>(
                nameof(FlowMemoryToParentActivity), input);

            if (!memoryFlowed)
            {
                return new CloningOutput(
                    Success: false,
                    Error: "Failed to flow memory back to parent");
            }
        }

        // Step 2: Destroy the clone (unregister, clean up state, remove from parent).
        var destroyed = await context.CallActivityAsync<bool>(
            nameof(DestroyCloneActivity), input);

        if (!destroyed)
        {
            return new CloningOutput(
                Success: false,
                Error: "Failed to destroy clone");
        }

        return new CloningOutput(
            Success: true,
            CloneId: input.TargetAgentId);
    }
}