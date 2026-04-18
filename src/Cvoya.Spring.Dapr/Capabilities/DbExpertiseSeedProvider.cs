// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Capabilities;

using System.Text.Json;

using Cvoya.Spring.Core.Capabilities;
using Cvoya.Spring.Dapr.Data;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

/// <summary>
/// Default <see cref="IExpertiseSeedProvider"/> — reads the persisted
/// <c>AgentDefinitions.Definition</c> / <c>UnitDefinitions.Definition</c>
/// JSON documents and extracts the <c>expertise:</c> block authored in the
/// YAML manifest. Used by <c>AgentActor.OnActivateAsync</c> and
/// <c>UnitActor.OnActivateAsync</c> to auto-seed actor state on first
/// activation (#488).
/// </summary>
/// <remarks>
/// <para>
/// Accepts both the user-facing YAML key (<c>domain:</c>) and the internal
/// key (<c>name:</c>) so callers can round-trip an expertise dump from
/// <c>GET /api/v1/agents/{id}/expertise</c> (which emits <c>name</c>) back
/// into a definition file without losing the seed.
/// </para>
/// <para>
/// Read failures (e.g. transient DB hiccup) surface as "no seed" — the actor
/// logs the warning and activates with empty expertise. The operator can
/// always push the seed through <c>PUT .../expertise</c> manually.
/// </para>
/// </remarks>
public class DbExpertiseSeedProvider(
    IServiceScopeFactory scopeFactory,
    ILoggerFactory loggerFactory) : IExpertiseSeedProvider
{
    private readonly ILogger _logger = loggerFactory.CreateLogger<DbExpertiseSeedProvider>();

    /// <inheritdoc />
    /// <remarks>
    /// <paramref name="agentId"/> is the Dapr actor id
    /// (<c>AgentDefinitionEntity.ActorId</c>). The sole production caller is
    /// <c>AgentActor.OnActivateAsync</c>, which passes <c>Id.GetId()</c> —
    /// always the actor GUID. Matching on <c>ActorId</c> alone avoids a
    /// latent collision where a row's <c>ActorId</c> (a GUID string) happens
    /// to equal another agent's user-facing <c>AgentId</c> (#519).
    /// </remarks>
    public async Task<IReadOnlyList<ExpertiseDomain>?> GetAgentSeedAsync(
        string agentId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(agentId))
        {
            return null;
        }

        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<SpringDbContext>();

            var entity = await db.AgentDefinitions
                .AsNoTracking()
                .FirstOrDefaultAsync(
                    a => a.ActorId == agentId && a.DeletedAt == null,
                    cancellationToken);

            return entity is null ? null : ExtractExpertise(entity.Definition);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Failed to read seed expertise for agent '{AgentId}'; treating as no seed.", agentId);
            return null;
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// <paramref name="unitId"/> is the Dapr actor id
    /// (<c>UnitDefinitionEntity.ActorId</c>). The sole production caller is
    /// <c>UnitActor.OnActivateAsync</c>, which passes <c>Id.GetId()</c> —
    /// always the actor GUID. Matching on <c>ActorId</c> alone avoids a
    /// latent collision where a row's <c>ActorId</c> (a GUID string) happens
    /// to equal another unit's user-facing <c>UnitId</c> (#519).
    /// </remarks>
    public async Task<IReadOnlyList<ExpertiseDomain>?> GetUnitSeedAsync(
        string unitId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(unitId))
        {
            return null;
        }

        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<SpringDbContext>();

            var entity = await db.UnitDefinitions
                .AsNoTracking()
                .FirstOrDefaultAsync(
                    u => u.ActorId == unitId && u.DeletedAt == null,
                    cancellationToken);

            return entity is null ? null : ExtractExpertise(entity.Definition);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Failed to read seed expertise for unit '{UnitId}'; treating as no seed.", unitId);
            return null;
        }
    }

    /// <summary>
    /// Pulls an <c>expertise:</c> array out of a persisted definition JSON
    /// document and maps each entry to an <see cref="ExpertiseDomain"/>.
    /// Returns <c>null</c> when no <c>expertise</c> property is present and
    /// an empty list when the property is present but empty — callers use
    /// that distinction to decide whether a seed was declared at all.
    /// </summary>
    internal static IReadOnlyList<ExpertiseDomain>? ExtractExpertise(JsonElement? definition)
    {
        if (definition is not { ValueKind: JsonValueKind.Object } element)
        {
            return null;
        }

        if (!element.TryGetProperty("expertise", out var expertise))
        {
            return null;
        }

        if (expertise.ValueKind != JsonValueKind.Array)
        {
            // Tolerate a scalar / object in the slot by treating it as "no
            // valid seed" rather than failing the activation outright.
            return null;
        }

        var result = new List<ExpertiseDomain>();
        foreach (var item in expertise.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            // YAML authors use `domain:` (matches the user-facing guide);
            // internal round-trips emit `name:` (matches ExpertiseDomain
            // on the wire). Accept either.
            string? name = null;
            if (item.TryGetProperty("name", out var nameProp) && nameProp.ValueKind == JsonValueKind.String)
            {
                name = nameProp.GetString();
            }
            else if (item.TryGetProperty("domain", out var domainProp) && domainProp.ValueKind == JsonValueKind.String)
            {
                name = domainProp.GetString();
            }

            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            string description = string.Empty;
            if (item.TryGetProperty("description", out var descProp) && descProp.ValueKind == JsonValueKind.String)
            {
                description = descProp.GetString() ?? string.Empty;
            }

            ExpertiseLevel? level = null;
            if (item.TryGetProperty("level", out var levelProp) && levelProp.ValueKind == JsonValueKind.String &&
                Enum.TryParse<ExpertiseLevel>(levelProp.GetString(), ignoreCase: true, out var parsed))
            {
                level = parsed;
            }

            result.Add(new ExpertiseDomain(name!, description, level));
        }

        return result;
    }
}