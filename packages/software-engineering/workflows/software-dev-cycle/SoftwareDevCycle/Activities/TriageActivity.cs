// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Packages.SoftwareEngineering.Workflows.Activities;

using Cvoya.Spring.Packages.SoftwareEngineering.Workflows.Models;
using Dapr.Workflow;
using Microsoft.Extensions.Logging;

/// <summary>
/// Classifies a work item by type and complexity, and identifies required expertise.
/// </summary>
public class TriageActivity(ILogger<TriageActivity> logger) : WorkflowActivity<DevCycleInput, TriageResult>
{
    public override Task<TriageResult> RunAsync(WorkflowActivityContext context, DevCycleInput input)
    {
        logger.LogInformation("Triaging work item: {Title}", input.Title);

        // Stub: return a placeholder triage result
        var result = new TriageResult(
            ItemType: "feature",
            Complexity: "medium",
            RequiredExpertise: ["backend-development"]);

        return Task.FromResult(result);
    }
}
