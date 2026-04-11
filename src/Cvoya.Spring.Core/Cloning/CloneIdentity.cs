// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Cloning;

/// <summary>
/// Identifies a clone and its relationship to the parent agent.
/// Stored in the clone actor's state to enable cost attribution and lifecycle management.
/// </summary>
/// <param name="ParentAgentId">The identifier of the agent that was cloned.</param>
/// <param name="CloneId">The unique identifier assigned to this clone.</param>
/// <param name="CloningPolicy">The memory policy used when creating this clone.</param>
/// <param name="AttachmentMode">Whether this clone is attached to or detached from the parent.</param>
public record CloneIdentity(
    string ParentAgentId,
    string CloneId,
    CloningPolicy CloningPolicy,
    AttachmentMode AttachmentMode);