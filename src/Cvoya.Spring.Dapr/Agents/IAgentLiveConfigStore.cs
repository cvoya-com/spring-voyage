// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Agents;

using Cvoya.Spring.Core.Agents;
using Cvoya.Spring.Core.Capabilities;

/// <summary>
/// Singleton seam over the scoped <c>IAgentLiveConfigRepository</c>.
/// <c>AgentActor</c> is not request-scoped, so it cannot consume the
/// scoped EF repository directly. The store creates a fresh DI scope
/// per call, resolves the repository, and forwards the operation.
/// Mirrors the scope-creating pattern already in use for
/// <c>IUnitHumanPermissionStore</c> and
/// <c>IUnitMemberGraphStore</c>.
/// </summary>
/// <remarks>
/// Registered as <c>TryAddSingleton</c> so cloud overlays can layer
/// audit logging or cross-tenant guards on top without displacing the
/// OSS default (per <c>AGENTS.md</c> § "Open-source platform and
/// extensibility").
/// </remarks>
public interface IAgentLiveConfigStore
{
    /// <summary>
    /// Returns the persisted metadata for <paramref name="agentId"/> in
    /// the current tenant. <see cref="AgentMetadata.ParentUnit"/> is
    /// always <c>null</c> — the membership table is the authoritative
    /// parent-unit pointer (ADR-0040).
    /// </summary>
    Task<AgentMetadata> GetMetadataAsync(Guid agentId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Applies the partial-PATCH semantics of
    /// <see cref="AgentMetadata"/>: only non-<c>null</c> fields are
    /// written. <see cref="AgentMetadata.ParentUnit"/> is ignored.
    /// Returns the field names that were actually written so the actor
    /// can build a meaningful <c>StateChanged</c> activity event.
    /// </summary>
    Task<IReadOnlyList<string>> UpsertMetadataAsync(
        Guid agentId, AgentMetadata metadata, CancellationToken cancellationToken = default);

    /// <summary>Returns the configured skill list, sorted by skill name.</summary>
    Task<string[]> GetSkillsAsync(Guid agentId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Replaces the configured skill list in full. Returns the
    /// normalised list that was actually persisted so the caller can
    /// emit it on the activity event.
    /// </summary>
    Task<string[]> SetSkillsAsync(
        Guid agentId, IReadOnlyList<string> skills, CancellationToken cancellationToken = default);

    /// <summary>Returns the configured expertise list, sorted by domain name.</summary>
    Task<ExpertiseDomain[]> GetExpertiseAsync(Guid agentId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Replaces the expertise list in full. Returns the normalised list
    /// that was actually persisted.
    /// </summary>
    Task<ExpertiseDomain[]> SetExpertiseAsync(
        Guid agentId, IReadOnlyList<ExpertiseDomain> domains, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns <c>true</c> when an expertise list has been written for
    /// <paramref name="agentId"/> at all (even an empty list). Used by
    /// the activation seeder to honour the "actor state wins"
    /// precedence rule (ADR-0040 / #488).
    /// </summary>
    Task<bool> HasExpertiseSetAsync(Guid agentId, CancellationToken cancellationToken = default);
}
