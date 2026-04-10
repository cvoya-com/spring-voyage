// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Workflows;

/// <summary>
/// Input for the <see cref="CloningLifecycleWorkflow"/>.
/// </summary>
/// <param name="SourceAgentId">The identifier of the agent to clone.</param>
/// <param name="TargetAgentId">The identifier for the new cloned agent.</param>
public record CloningInput(
    string SourceAgentId,
    string TargetAgentId);
