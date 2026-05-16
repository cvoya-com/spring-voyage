// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Data;

using Cvoya.Spring.Core.Agents;
using Cvoya.Spring.Core.Capabilities;

/// <summary>
/// Persistence abstraction for the agent live-config tables introduced
/// in #2048 / ADR-0040: <c>agent_live_config</c> (1:1),
/// <c>agent_tool_grants</c> (N, #2335 Sub B), and <c>agent_expertise</c> (N).
/// Encapsulates the single-row-per-(tenant, agent, key) invariants so
/// the actor write path and any read consumer agree on the schema
/// without taking a direct dependency on <see cref="SpringDbContext"/>.
/// </summary>
/// <remarks>
/// The interface lives in <c>Cvoya.Spring.Dapr.Data</c> rather than
/// <c>Cvoya.Spring.Core</c> because <see cref="AgentMetadata"/> already
/// crosses the Dapr boundary and the implementation is EF-specific. Cloud
/// overlays may swap the implementation through DI (TryAdd) without
/// touching consumers.
/// </remarks>
public interface IAgentLiveConfigRepository
{
    /// <summary>
    /// Returns the persisted metadata for <paramref name="agentId"/> in
    /// the current tenant. <see cref="AgentMetadata.ParentUnit"/> is
    /// always returned as <c>null</c> — the membership table is the
    /// authoritative parent-unit pointer (ADR-0040). Returns an
    /// all-<c>null</c> record (with <see cref="AgentMetadata.Enabled"/>
    /// also <c>null</c>) when no row exists yet.
    /// </summary>
    Task<AgentMetadata> GetMetadataAsync(Guid agentId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Applies the partial-PATCH semantics of
    /// <see cref="AgentMetadata"/>: only non-<c>null</c> fields are
    /// written. <see cref="AgentMetadata.ParentUnit"/> is ignored — the
    /// membership table owns that pointer (ADR-0040). Creates a row when
    /// none exists; otherwise updates the existing row in place. Returns
    /// the list of field names that were actually written so the caller
    /// can build a meaningful <c>StateChanged</c> activity event.
    /// </summary>
    Task<IReadOnlyList<string>> UpsertMetadataAsync(
        Guid agentId, AgentMetadata metadata, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the configured skill list for <paramref name="agentId"/>
    /// in the current tenant, sorted by skill name. Empty when no row
    /// exists or the agent has no granted skills.
    /// </summary>
    Task<string[]> GetSkillsAsync(Guid agentId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Replaces the configured skill list for <paramref name="agentId"/>
    /// with <paramref name="skills"/> in full. Empty list is a legitimate
    /// "disable everything" state. Skills are normalised: trimmed,
    /// whitespace dropped, deduplicated case-insensitively, sorted.
    /// </summary>
    Task<string[]> SetSkillsAsync(
        Guid agentId, IReadOnlyList<string> skills, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the configured expertise list for
    /// <paramref name="agentId"/> in the current tenant, sorted by
    /// domain name. Empty when no row exists or the agent has no
    /// expertise declared.
    /// </summary>
    Task<ExpertiseDomain[]> GetExpertiseAsync(Guid agentId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Replaces the expertise list for <paramref name="agentId"/> with
    /// <paramref name="domains"/> in full. Empty list is a legitimate
    /// "no expertise" state. Domains are deduplicated case-insensitively
    /// on <see cref="ExpertiseDomain.Name"/> with last-write-wins, and
    /// sorted by name.
    /// </summary>
    Task<ExpertiseDomain[]> SetExpertiseAsync(
        Guid agentId, IReadOnlyList<ExpertiseDomain> domains, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns <c>true</c> when an expertise list has been written for
    /// <paramref name="agentId"/> at all (even an empty list). Used by
    /// the activation seeder to honour the "actor state wins" precedence
    /// rule (ADR-0040 / #488) — once any value has been persisted, the
    /// YAML seed is not re-applied.
    /// </summary>
    Task<bool> HasExpertiseSetAsync(Guid agentId, CancellationToken cancellationToken = default);
}
