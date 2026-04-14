// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Skills;

using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

using Cvoya.Spring.Core.Skills;
using Cvoya.Spring.Core.State;

/// <summary>
/// OSS default <see cref="IUnitSkillBundleStore"/>. Persists the bundle list
/// as a single JSON document per unit under a deterministic key in the shared
/// <see cref="IStateStore"/>. A JSON document is sufficient because the whole
/// list is always read and written together — individual bundles are never
/// updated in place.
/// </summary>
public class StateStoreBackedUnitSkillBundleStore : IUnitSkillBundleStore
{
    private const string KeyPrefix = "Unit:SkillBundles:";

    private readonly IStateStore _stateStore;

    /// <summary>
    /// Creates a new <see cref="StateStoreBackedUnitSkillBundleStore"/>.
    /// </summary>
    public StateStoreBackedUnitSkillBundleStore(IStateStore stateStore)
    {
        _stateStore = stateStore;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<SkillBundle>> GetAsync(
        string unitId,
        CancellationToken cancellationToken = default)
    {
        var record = await _stateStore.GetAsync<UnitSkillBundleRecord>(BuildKey(unitId), cancellationToken).ConfigureAwait(false);
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

    /// <inheritdoc />
    public Task SetAsync(
        string unitId,
        IReadOnlyList<SkillBundle> bundles,
        CancellationToken cancellationToken = default)
    {
        var record = new UnitSkillBundleRecord(
            Bundles: bundles.Select(b => new SkillBundleRecord(
                PackageName: b.PackageName,
                SkillName: b.SkillName,
                Prompt: b.Prompt,
                RequiredTools: b.RequiredTools.Select(t => new SkillToolRequirementRecord(
                    Name: t.Name,
                    Description: t.Description,
                    Schema: t.Schema,
                    Optional: t.Optional)).ToList())).ToList());

        return _stateStore.SetAsync(BuildKey(unitId), record, cancellationToken);
    }

    /// <inheritdoc />
    public Task DeleteAsync(string unitId, CancellationToken cancellationToken = default) =>
        _stateStore.DeleteAsync(BuildKey(unitId), cancellationToken);

    private static string BuildKey(string unitId) => KeyPrefix + unitId;

    /// <summary>
    /// Serialised shape. Kept separate from <see cref="SkillBundle"/> so the
    /// on-the-wire format can evolve independently of the Core record.
    /// </summary>
    public sealed record UnitSkillBundleRecord(List<SkillBundleRecord> Bundles);

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