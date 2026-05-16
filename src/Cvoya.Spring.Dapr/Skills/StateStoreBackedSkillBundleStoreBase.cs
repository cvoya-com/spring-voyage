// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Skills;

using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

using Cvoya.Spring.Core.Skills;
using Cvoya.Spring.Core.State;

/// <summary>
/// Shared implementation for the unit- and agent-keyed skill-bundle stores.
/// Persists the bundle list as a single JSON document per subject under a
/// deterministic key in the shared <see cref="IStateStore"/>. A JSON
/// document is sufficient because the whole list is always read and
/// written together — individual bundles are never updated in place.
/// </summary>
/// <remarks>
/// The two concrete subclasses differ only in the per-subject key prefix:
/// <c>Unit:SkillBundles:</c> for <see cref="StateStoreBackedUnitSkillBundleStore"/>
/// and <c>Agent:SkillBundles:</c> for
/// <see cref="StateStoreBackedAgentSkillBundleStore"/>. All mutation
/// methods re-resolve the supplied
/// <see cref="SkillBundleReference"/> values through
/// <see cref="ISkillBundleResolver"/> so the persisted record carries the
/// freshest prompt + required-tools snapshot, matching today's
/// <c>UnitCreationService.ResolveSkillBundlesAsync</c> shape.
/// </remarks>
public abstract class StateStoreBackedSkillBundleStoreBase
{
    private readonly IStateStore _stateStore;
    private readonly ISkillBundleResolver _resolver;

    /// <summary>
    /// Creates a new <see cref="StateStoreBackedSkillBundleStoreBase"/>.
    /// </summary>
    protected StateStoreBackedSkillBundleStoreBase(
        IStateStore stateStore,
        ISkillBundleResolver resolver)
    {
        ArgumentNullException.ThrowIfNull(stateStore);
        ArgumentNullException.ThrowIfNull(resolver);

        _stateStore = stateStore;
        _resolver = resolver;
    }

    /// <summary>The prefix used to build the state-store key for this store's subject.</summary>
    protected abstract string KeyPrefix { get; }

    /// <summary>
    /// Reads the persisted bundle list for the given subject. Returns an
    /// empty list when no record has been written.
    /// </summary>
    protected async Task<IReadOnlyList<SkillBundle>> GetCoreAsync(
        string subjectId,
        CancellationToken cancellationToken)
    {
        var record = await _stateStore
            .GetAsync<SubjectSkillBundleRecord>(BuildKey(subjectId), cancellationToken)
            .ConfigureAwait(false);
        return MaterialiseFromRecord(record);
    }

    /// <summary>
    /// Re-resolves <paramref name="references"/> and replaces the
    /// subject's bundle list wholesale.
    /// </summary>
    protected async Task<IReadOnlyList<SkillBundle>> SetCoreAsync(
        string subjectId,
        IReadOnlyList<SkillBundleReference> references,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(references);

        var resolved = new List<SkillBundle>(references.Count);
        foreach (var reference in references)
        {
            resolved.Add(await _resolver.ResolveAsync(reference, cancellationToken).ConfigureAwait(false));
        }

        await PersistAsync(subjectId, resolved, cancellationToken).ConfigureAwait(false);
        return resolved;
    }

    /// <summary>
    /// Re-resolves <paramref name="reference"/> and appends it to the
    /// subject's list. When a bundle with the same
    /// <c>(packageName, skillName)</c> already exists the entry is
    /// re-resolved in place so its prompt + required-tools snapshot is
    /// refreshed without reordering.
    /// </summary>
    protected async Task<IReadOnlyList<SkillBundle>> AddCoreAsync(
        string subjectId,
        SkillBundleReference reference,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(reference);

        var resolved = await _resolver.ResolveAsync(reference, cancellationToken).ConfigureAwait(false);

        var existing = await GetCoreAsync(subjectId, cancellationToken).ConfigureAwait(false);
        var list = new List<SkillBundle>(existing.Count + 1);

        var replaced = false;
        foreach (var item in existing)
        {
            if (!replaced
                && string.Equals(item.PackageName, resolved.PackageName, StringComparison.Ordinal)
                && string.Equals(item.SkillName, resolved.SkillName, StringComparison.Ordinal))
            {
                list.Add(resolved);
                replaced = true;
            }
            else
            {
                list.Add(item);
            }
        }

        if (!replaced)
        {
            list.Add(resolved);
        }

        await PersistAsync(subjectId, list, cancellationToken).ConfigureAwait(false);
        return list;
    }

    /// <summary>
    /// Removes the bundle identified by <paramref name="packageName"/> +
    /// <paramref name="skillName"/>. No-op when no matching entry exists.
    /// </summary>
    protected async Task<IReadOnlyList<SkillBundle>> RemoveCoreAsync(
        string subjectId,
        string packageName,
        string skillName,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packageName);
        ArgumentException.ThrowIfNullOrWhiteSpace(skillName);

        var existing = await GetCoreAsync(subjectId, cancellationToken).ConfigureAwait(false);
        if (existing.Count == 0)
        {
            return existing;
        }

        var filtered = existing
            .Where(b => !(string.Equals(b.PackageName, packageName, StringComparison.Ordinal)
                       && string.Equals(b.SkillName, skillName, StringComparison.Ordinal)))
            .ToList();

        if (filtered.Count == existing.Count)
        {
            // Nothing to remove — leave the store untouched so we don't
            // churn the JSON doc on a no-op call.
            return existing;
        }

        await PersistAsync(subjectId, filtered, cancellationToken).ConfigureAwait(false);
        return filtered;
    }

    /// <summary>Deletes the subject's bundle record entirely.</summary>
    protected Task DeleteCoreAsync(string subjectId, CancellationToken cancellationToken) =>
        _stateStore.DeleteAsync(BuildKey(subjectId), cancellationToken);

    private Task PersistAsync(
        string subjectId,
        IReadOnlyList<SkillBundle> bundles,
        CancellationToken cancellationToken)
    {
        var record = new SubjectSkillBundleRecord(
            Bundles: bundles.Select(b => new SkillBundleRecord(
                PackageName: b.PackageName,
                SkillName: b.SkillName,
                Prompt: b.Prompt,
                RequiredTools: b.RequiredTools.Select(t => new SkillToolRequirementRecord(
                    Name: t.Name,
                    Description: t.Description,
                    Schema: t.Schema,
                    Optional: t.Optional)).ToList())).ToList());

        return _stateStore.SetAsync(BuildKey(subjectId), record, cancellationToken);
    }

    private static IReadOnlyList<SkillBundle> MaterialiseFromRecord(SubjectSkillBundleRecord? record)
    {
        if (record?.Bundles is null || record.Bundles.Count == 0)
        {
            return Array.Empty<SkillBundle>();
        }

        return record.Bundles
            .Select(b => new SkillBundle(
                PackageName: b.PackageName,
                SkillName: b.SkillName,
                Prompt: b.Prompt,
                RequiredTools: (IReadOnlyList<SkillToolRequirement>)b.RequiredTools
                    .Select(t => new SkillToolRequirement(
                        Name: t.Name,
                        Description: t.Description,
                        Schema: t.Schema,
                        Optional: t.Optional))
                    .ToList()))
            .ToList();
    }

    private string BuildKey(string subjectId) => KeyPrefix + subjectId;

    /// <summary>
    /// Serialised shape. Kept separate from <see cref="SkillBundle"/> so
    /// the on-the-wire format can evolve independently of the Core record.
    /// </summary>
    public sealed record SubjectSkillBundleRecord(List<SkillBundleRecord> Bundles);

    /// <summary>Serialised bundle row.</summary>
    public sealed record SkillBundleRecord(
        string PackageName,
        string SkillName,
        string Prompt,
        List<SkillToolRequirementRecord> RequiredTools);

    /// <summary>Serialised tool-requirement row.</summary>
    public sealed record SkillToolRequirementRecord(
        string Name,
        string Description,
        [property: JsonConverter(typeof(JsonElementConverter))] JsonElement Schema,
        bool Optional);

    /// <summary>
    /// Preserves the <see cref="JsonElement"/> schema across round-trips.
    /// Default System.Text.Json handling already supports JsonElement; this
    /// converter is a defensive no-op that documents the contract.
    /// </summary>
    private sealed class JsonElementConverter : JsonConverter<JsonElement>
    {
        public override JsonElement Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            using var doc = JsonDocument.ParseValue(ref reader);
            return doc.RootElement.Clone();
        }

        public override void Write(Utf8JsonWriter writer, JsonElement value, JsonSerializerOptions options)
        {
            value.WriteTo(writer);
        }
    }
}
