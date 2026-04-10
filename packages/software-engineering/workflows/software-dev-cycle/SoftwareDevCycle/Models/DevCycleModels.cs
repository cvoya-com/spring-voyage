// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Packages.SoftwareEngineering.Workflows.Models;

/// <summary>
/// Input for the software development cycle workflow.
/// </summary>
/// <param name="Title">Title of the work item.</param>
/// <param name="Description">Detailed description of the work.</param>
/// <param name="Source">Origin of the work item (e.g., "github-issue").</param>
/// <param name="SourceUrl">URL to the source work item.</param>
public record DevCycleInput(string Title, string Description, string Source, string SourceUrl);

/// <summary>
/// Output of the software development cycle workflow.
/// </summary>
/// <param name="Success">Whether the cycle completed successfully.</param>
/// <param name="PrUrl">URL of the merged pull request, if any.</param>
/// <param name="Summary">Human-readable summary of the outcome.</param>
public record DevCycleOutput(bool Success, string? PrUrl, string Summary);

/// <summary>
/// Result of the triage step.
/// </summary>
/// <param name="ItemType">Classified type: feature, bug, refactor, documentation.</param>
/// <param name="Complexity">Estimated complexity: small, medium, large.</param>
/// <param name="RequiredExpertise">List of expertise domains needed.</param>
public record TriageResult(string ItemType, string Complexity, IReadOnlyList<string> RequiredExpertise);

/// <summary>
/// Reference to an agent.
/// </summary>
/// <param name="AgentId">Unique identifier of the agent.</param>
/// <param name="Role">Role of the agent (e.g., "backend-engineer").</param>
public record AgentRef(string AgentId, string Role);

/// <summary>
/// An implementation plan for a work item.
/// </summary>
/// <param name="Steps">Ordered list of plan steps.</param>
/// <param name="EstimatedEffort">Estimated effort description.</param>
public record Plan(IReadOnlyList<string> Steps, string EstimatedEffort);

/// <summary>
/// Input for the plan creation activity.
/// </summary>
/// <param name="DevCycleInput">The original work item input.</param>
/// <param name="TriageResult">Result of the triage step.</param>
/// <param name="Assignee">Agent assigned to the work.</param>
public record PlanInput(DevCycleInput DevCycleInput, TriageResult TriageResult, AgentRef Assignee);

/// <summary>
/// Input for the implementation activity.
/// </summary>
/// <param name="DevCycleInput">The original work item input.</param>
/// <param name="Plan">The approved plan.</param>
/// <param name="Assignee">Agent performing the implementation.</param>
public record ImplInput(DevCycleInput DevCycleInput, Plan Plan, AgentRef Assignee);

/// <summary>
/// Result of a pull request creation.
/// </summary>
/// <param name="PrUrl">URL of the created pull request.</param>
/// <param name="Branch">Branch name for the PR.</param>
public record PrResult(string PrUrl, string Branch);

/// <summary>
/// Result of a code review.
/// </summary>
/// <param name="Decision">Review decision: approve, request-changes, comment.</param>
/// <param name="Comments">Review comments or feedback.</param>
public record ReviewResult(string Decision, string Comments);

/// <summary>
/// Approval decision for a plan.
/// </summary>
/// <param name="Approved">Whether the plan was approved.</param>
/// <param name="Feedback">Feedback from the approver, if any.</param>
public record Approval(bool Approved, string? Feedback);
