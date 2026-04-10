// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Workflows;

using global::Dapr.Workflow;

/// <summary>
/// Dapr Workflow stub for agent cloning. Returns a not-implemented result in Phase 1.
/// </summary>
public class CloningLifecycleWorkflow : Workflow<CloningInput, CloningOutput>
{
    /// <inheritdoc />
    public override Task<CloningOutput> RunAsync(WorkflowContext context, CloningInput input)
    {
        return Task.FromResult(new CloningOutput(
            Success: false, Error: "Cloning not implemented in Phase 1"));
    }
}
