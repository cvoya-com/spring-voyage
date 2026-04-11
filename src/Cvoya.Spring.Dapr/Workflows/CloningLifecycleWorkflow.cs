// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Workflows;

using Cvoya.Spring.Dapr.Workflows.Activities;

using global::Dapr.Workflow;

/// <summary>
/// Dapr Workflow that orchestrates the full lifecycle of an ephemeral agent clone:
/// validate the request, create the clone actor, and register it in the directory.
/// </summary>
public class CloningLifecycleWorkflow : Workflow<CloningInput, CloningOutput>
{
    /// <inheritdoc />
    public override async Task<CloningOutput> RunAsync(WorkflowContext context, CloningInput input)
    {
        // Step 1: Validate the clone request.
        var isValid = await context.CallActivityAsync<bool>(
            nameof(ValidateCloneRequestActivity), input);

        if (!isValid)
        {
            return new CloningOutput(
                Success: false,
                Error: "Clone request validation failed");
        }

        // Step 2: Create the clone actor and copy state if needed.
        var created = await context.CallActivityAsync<bool>(
            nameof(CreateCloneActorActivity), input);

        if (!created)
        {
            return new CloningOutput(
                Success: false,
                Error: "Failed to create clone actor");
        }

        // Step 3: Register the clone in the directory.
        var registered = await context.CallActivityAsync<bool>(
            nameof(RegisterCloneActivity), input);

        if (!registered)
        {
            return new CloningOutput(
                Success: false,
                Error: "Failed to register clone in directory");
        }

        return new CloningOutput(
            Success: true,
            CloneAgentAddress: $"agent/{input.TargetAgentId}",
            CloneId: input.TargetAgentId);
    }
}