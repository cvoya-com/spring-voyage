// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Skills;

using System.Collections.Generic;

using Cvoya.Spring.Core.Skills;
using Cvoya.Spring.Core.State;

/// <summary>
/// OSS default <see cref="IAgentSkillBundleStore"/>. Persists the agent's
/// equipped bundle list as a single JSON document under a deterministic
/// key in the shared <see cref="IStateStore"/>. The bundle prompts feed
/// Layer 4 (agent instructions) of the assembled prompt — distinct from
/// the unit-scoped store which feeds Layer 2.
/// </summary>
public sealed class StateStoreBackedAgentSkillBundleStore
    : StateStoreBackedSkillBundleStoreBase, IAgentSkillBundleStore
{
    /// <summary>
    /// Creates a new <see cref="StateStoreBackedAgentSkillBundleStore"/>.
    /// </summary>
    public StateStoreBackedAgentSkillBundleStore(
        IStateStore stateStore,
        ISkillBundleResolver resolver)
        : base(stateStore, resolver)
    {
    }

    /// <inheritdoc />
    protected override string KeyPrefix => "Agent:SkillBundles:";

    /// <inheritdoc />
    public Task<IReadOnlyList<SkillBundle>> GetAsync(
        string agentId,
        CancellationToken cancellationToken = default) =>
        GetCoreAsync(agentId, cancellationToken);

    /// <inheritdoc />
    public Task<IReadOnlyList<SkillBundle>> SetAsync(
        string agentId,
        IReadOnlyList<SkillBundleReference> references,
        CancellationToken cancellationToken = default) =>
        SetCoreAsync(agentId, references, cancellationToken);

    /// <inheritdoc />
    public Task<IReadOnlyList<SkillBundle>> AddAsync(
        string agentId,
        SkillBundleReference reference,
        CancellationToken cancellationToken = default) =>
        AddCoreAsync(agentId, reference, cancellationToken);

    /// <inheritdoc />
    public Task<IReadOnlyList<SkillBundle>> RemoveAsync(
        string agentId,
        string packageName,
        string skillName,
        CancellationToken cancellationToken = default) =>
        RemoveCoreAsync(agentId, packageName, skillName, cancellationToken);

    /// <inheritdoc />
    public Task DeleteAsync(string agentId, CancellationToken cancellationToken = default) =>
        DeleteCoreAsync(agentId, cancellationToken);
}
