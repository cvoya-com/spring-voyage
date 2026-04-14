// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Data;

using System.Text.Json;

using Cvoya.Spring.Core.Policies;
using Cvoya.Spring.Dapr.Data.Entities;

using Microsoft.EntityFrameworkCore;

/// <summary>
/// EF Core-backed implementation of <see cref="IUnitPolicyRepository"/>.
/// Persists rows in <c>unit_policies</c>; one row per unit. Empty policies
/// (no dimensions set) are represented as row deletions to keep the table
/// "units that actually have a policy" rather than "every unit".
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

        return new UnitPolicy(Skill: DeserializeSkill(entity.Skill));
    }

    /// <inheritdoc />
    public async Task SetAsync(string unitId, UnitPolicy policy, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(unitId);
        ArgumentNullException.ThrowIfNull(policy);

        // An all-null policy carries no constraint — delete the row rather
        // than keep an inert marker around.
        if (policy.Skill is null)
        {
            await DeleteAsync(unitId, cancellationToken);
            return;
        }

        var existing = await context.UnitPolicies
            .FirstOrDefaultAsync(p => p.UnitId == unitId, cancellationToken);

        var skillJson = SerializeSkill(policy.Skill);

        if (existing is null)
        {
            context.UnitPolicies.Add(new UnitPolicyEntity
            {
                UnitId = unitId,
                Skill = skillJson,
            });
        }
        else
        {
            existing.Skill = skillJson;
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

    private static JsonElement? SerializeSkill(SkillPolicy? skill)
    {
        if (skill is null)
        {
            return null;
        }

        return JsonSerializer.SerializeToElement(skill);
    }

    private static SkillPolicy? DeserializeSkill(JsonElement? skill)
    {
        if (skill is null || skill.Value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return null;
        }

        return JsonSerializer.Deserialize<SkillPolicy>(skill.Value.GetRawText());
    }
}