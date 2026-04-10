// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Packages.SoftwareEngineering.Workflows.Activities;

using Cvoya.Spring.Packages.SoftwareEngineering.Workflows.Models;
using Dapr.Workflow;
using Microsoft.Extensions.Logging;

/// <summary>
/// Dispatches implementation work to the assigned agent and collects the resulting PR.
/// </summary>
public class ImplementActivity(ILogger<ImplementActivity> logger) : WorkflowActivity<ImplInput, PrResult>
{
    public override Task<PrResult> RunAsync(WorkflowActivityContext context, ImplInput input)
    {
        logger.LogInformation(
            "Implementing: {Title} (agent: {AgentId}, steps: {StepCount})",
            input.DevCycleInput.Title,
            input.Assignee.AgentId,
            input.Plan.Steps.Count);

        // Stub: return a placeholder PR result
        var result = new PrResult(
            PrUrl: $"{input.DevCycleInput.SourceUrl}/pull/0",
            Branch: "feature/placeholder");

        return Task.FromResult(result);
    }
}
