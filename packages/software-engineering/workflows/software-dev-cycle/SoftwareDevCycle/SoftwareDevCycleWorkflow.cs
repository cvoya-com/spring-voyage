// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Packages.SoftwareEngineering.Workflows;

using Cvoya.Spring.Packages.SoftwareEngineering.Workflows.Activities;
using Cvoya.Spring.Packages.SoftwareEngineering.Workflows.Models;
using Dapr.Workflow;

/// <summary>
/// Dapr Workflow that orchestrates the full software development cycle:
/// triage, assign, plan, approve, implement, review, merge.
/// </summary>
public class SoftwareDevCycleWorkflow : Workflow<DevCycleInput, DevCycleOutput>
{
    public override async Task<DevCycleOutput> RunAsync(WorkflowContext context, DevCycleInput input)
    {
        // Step 1: Triage the work item
        var triageResult = await context.CallActivityAsync<TriageResult>(
            nameof(TriageActivity),
            input);

        // Step 2: Assign to the best-fit agent by expertise
        var assignee = await context.CallActivityAsync<AgentRef>(
            nameof(AssignByExpertiseActivity),
            triageResult);

        // Step 3: Create an implementation plan
        var planInput = new PlanInput(input, triageResult, assignee);
        var plan = await context.CallActivityAsync<Plan>(
            nameof(CreatePlanActivity),
            planInput);

        // Step 4: Wait for plan approval (external event from tech lead or human)
        var approval = await context.WaitForExternalEventAsync<Approval>("PlanApproval");

        if (!approval.Approved)
        {
            return new DevCycleOutput(
                Success: false,
                PrUrl: null,
                Summary: $"Plan was not approved. Feedback: {approval.Feedback}");
        }

        // Step 5: Implement the plan
        var implInput = new ImplInput(input, plan, assignee);
        var prResult = await context.CallActivityAsync<PrResult>(
            nameof(ImplementActivity),
            implInput);

        // Step 6: Review the pull request
        var reviewResult = await context.CallActivityAsync<ReviewResult>(
            nameof(ReviewActivity),
            prResult);

        if (reviewResult.Decision != "approve")
        {
            return new DevCycleOutput(
                Success: false,
                PrUrl: prResult.PrUrl,
                Summary: $"Review decision: {reviewResult.Decision}. Comments: {reviewResult.Comments}");
        }

        // Step 7: Merge the pull request
        await context.CallActivityAsync<bool>(
            nameof(MergeActivity),
            prResult);

        return new DevCycleOutput(
            Success: true,
            PrUrl: prResult.PrUrl,
            Summary: $"Successfully completed {triageResult.ItemType} work item and merged PR.");
    }
}
