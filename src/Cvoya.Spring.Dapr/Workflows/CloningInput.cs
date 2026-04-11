// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Workflows;

using Cvoya.Spring.Core.Cloning;

/// <summary>
/// Input for the <see cref="CloningLifecycleWorkflow"/>.
/// </summary>
/// <param name="SourceAgentId">The identifier of the agent to clone.</param>
/// <param name="TargetAgentId">The identifier for the new cloned agent.</param>
/// <param name="CloningPolicy">The memory policy for the clone.</param>
/// <param name="AttachmentMode">Whether the clone is attached to or detached from the parent.</param>
/// <param name="Budget">Optional budget limit for the clone's operations.</param>
/// <param name="MaxClones">Optional maximum number of active clones allowed for the parent agent.</param>
public record CloningInput(
    string SourceAgentId,
    string TargetAgentId,
    CloningPolicy CloningPolicy = CloningPolicy.EphemeralNoMemory,
    AttachmentMode AttachmentMode = AttachmentMode.Detached,
    decimal? Budget = null,
    int? MaxClones = null);