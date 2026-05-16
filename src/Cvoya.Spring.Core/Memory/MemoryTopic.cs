// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Memory;

using Cvoya.Spring.Core.Messaging;

/// <summary>
/// A topic grouping zero or more <see cref="MemoryEntry"/> records owned
/// by the same addressable. Topics are owner-scoped — the
/// <c>(tenant, owner_scheme, owner_id, name)</c> tuple is unique so an
/// agent and a unit may share a topic name without conflict.
/// </summary>
/// <param name="Id">Stable Guid identifier for the topic.</param>
/// <param name="Owner">Address of the owning agent or unit.</param>
/// <param name="Name">
/// Owner-unique human-readable topic name. Used by the LLM to refer to
/// the topic without holding the Guid.
/// </param>
/// <param name="Description">
/// Optional free-text description. Returned alongside the topic in
/// search and list operations so the LLM can pick the right topic
/// without a follow-up read.
/// </param>
/// <param name="CreatedAt">UTC timestamp the topic was created.</param>
/// <param name="UpdatedAt">
/// UTC timestamp of the last name / description mutation; equal to
/// <see cref="CreatedAt"/> for topics that have never been updated.
/// </param>
public record MemoryTopic(
    Guid Id,
    Address Owner,
    string Name,
    string? Description,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);
