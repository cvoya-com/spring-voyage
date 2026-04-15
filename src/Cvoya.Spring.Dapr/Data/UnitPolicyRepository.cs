// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Data;

using System.Text.Json;

using Cvoya.Spring.Core.Initiative;
using Cvoya.Spring.Core.Policies;
using Cvoya.Spring.Dapr.Data.Entities;

using Microsoft.EntityFrameworkCore;

/// <summary>
/// EF Core-backed implementation of <see cref="IUnitPolicyRepository"/>.
/// Persists rows in <c>unit_policies</c>; one row per unit. An all-null
/// policy (<see cref="UnitPolicy.IsEmpty"/>) is represented as a row deletion
/// so the table stays "units that actually have a policy".
/// </summary>
public class UnitPolicyRepository(SpringDbContext context) : IUnitPolicyRepository
{
    /// <inheritdoc />
    public async Task<UnitPolicy> GetAsync(string unitId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(unitId);

        var entity = await context.UnitPolicies
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.UnitId == unitId, cancellationToken);

        if (entity is null)
        {
            return UnitPolicy.Empty;
        }

        return new UnitPolicy(
            Skill: Deserialize<SkillPolicy>(entity.Skill),
            Model: Deserialize<ModelPolicy>(entity.Model),
            Cost: Deserialize<CostPolicy>(entity.Cost),
            ExecutionMode: Deserialize<ExecutionModePolicy>(entity.ExecutionMode),
            Initiative: Deserialize<InitiativePolicy>(entity.Initiative));
    }

    /// <inheritdoc />
    public async Task SetAsync(string unitId, UnitPolicy policy, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(unitId);
        ArgumentNullException.ThrowIfNull(policy);

        // An all-null policy carries no constraint — delete the row rather
        // than keep an inert marker around.
        if (policy.IsEmpty)
        {
            await DeleteAsync(unitId, cancellationToken);
            return;
        }

        var existing = await context.UnitPolicies
            .FirstOrDefaultAsync(p => p.UnitId == unitId, cancellationToken);

        var skill = Serialize(policy.Skill);
        var model = Serialize(policy.Model);
        var cost = Serialize(policy.Cost);
        var execMode = Serialize(policy.ExecutionMode);
        var initiative = Serialize(policy.Initiative);

        if (existing is null)
        {
            context.UnitPolicies.Add(new UnitPolicyEntity
            {
                UnitId = unitId,
                Skill = skill,
                Model = model,
                Cost = cost,
                ExecutionMode = execMode,
                Initiative = initiative,
            });
        }
        else
        {
            existing.Skill = skill;
            existing.Model = model;
            existing.Cost = cost;
            existing.ExecutionMode = execMode;
            existing.Initiative = initiative;
        }

        await context.SaveChangesAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task DeleteAsync(string unitId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(unitId);

        var existing = await context.UnitPolicies
            .FirstOrDefaultAsync(p => p.UnitId == unitId, cancellationToken);

        if (existing is null)
        {
            return;
        }

        context.UnitPolicies.Remove(existing);
        await context.SaveChangesAsync(cancellationToken);
    }

    private static JsonElement? Serialize<T>(T? value) where T : class
    {
        if (value is null)
        {
            return null;
        }

        return JsonSerializer.SerializeToElement(value);
    }

    private static T? Deserialize<T>(JsonElement? element) where T : class
    {
        if (element is null || element.Value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return null;
        }

        return JsonSerializer.Deserialize<T>(element.Value.GetRawText());
    }
}