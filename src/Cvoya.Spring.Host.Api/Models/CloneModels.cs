// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Api.Models;

using Cvoya.Spring.Core.Cloning;

/// <summary>Request body for creating a clone.</summary>
/// <param name="CloneType">Clone lifecycle type.</param>
/// <param name="AttachmentMode">How the clone relates to the parent.</param>
public record CreateCloneRequest(CloningPolicy CloneType, AttachmentMode AttachmentMode);

/// <summary>Response body representing a clone.</summary>
/// <param name="CloneId">The unique identifier for the clone.</param>
/// <param name="ParentAgentId">The parent agent's identifier.</param>
/// <param name="CloneType">The clone lifecycle type.</param>
/// <param name="AttachmentMode">The attachment mode.</param>
/// <param name="Status">The current clone status.</param>
/// <param name="CreatedAt">When the clone was created.</param>
public record CloneResponse(
    string CloneId,
    string ParentAgentId,
    CloningPolicy CloneType,
    AttachmentMode AttachmentMode,
    string Status,
    DateTimeOffset CreatedAt);