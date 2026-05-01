// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Api.Services;

using Cvoya.Spring.Dapr.Data;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

/// <summary>
/// Default scoped implementation of <see cref="IParticipantDisplayNameResolver"/>.
/// Looks up display names from <c>AgentDefinitions</c> and <c>UnitDefinitions</c>
/// tables for the <c>agent://</c> and <c>unit://</c> schemes respectively.
/// For <c>human://</c> the path component itself is the user-id, and the
/// display name comes from the same source as <c>UserProfileResponse.DisplayName</c>
/// (the authenticated user's name claim). Because that claim is not stored in
/// a queryable table, human display names fall back to the user-id path — this
/// is the same slug the portal currently renders and is acceptable until a
/// dedicated profile store ships.
///
/// Results are cached in a per-request dictionary so repeated calls for the
/// same address (e.g. a single agent appearing as the <c>from</c> on multiple
/// inbox rows) issue at most one database round-trip.
/// </summary>
internal sealed class ParticipantDisplayNameResolver(
    SpringDbContext db,
    ILogger<ParticipantDisplayNameResolver> logger)
    : IParticipantDisplayNameResolver
{
    private readonly Dictionary<string, string> _cache = new(StringComparer.Ordinal);

    /// <inheritdoc />
    public async ValueTask<string> ResolveAsync(
        string address,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(address))
        {
            return address;
        }

        if (_cache.TryGetValue(address, out var cached))
        {
            return cached;
        }

        var result = await ResolveInternalAsync(address, cancellationToken);
        _cache[address] = result;
        return result;
    }

    private async Task<string> ResolveInternalAsync(
        string address,
        CancellationToken cancellationToken)
    {
        var separatorIdx = address.IndexOf("://", StringComparison.Ordinal);
        string scheme;
        string path;

        if (separatorIdx > 0)
        {
            scheme = address[..separatorIdx];
            path = address[(separatorIdx + 3)..];
        }
        else
        {
            // No scheme separator — return as-is (defensive).
            return address;
        }

        if (string.IsNullOrWhiteSpace(path))
        {
            return address;
        }

        try
        {
            if (string.Equals(scheme, "agent", StringComparison.OrdinalIgnoreCase))
            {
                var name = await db.AgentDefinitions
                    .AsNoTracking()
                    .Where(a => a.AgentId == path && a.DeletedAt == null)
                    .Select(a => a.Name)
                    .FirstOrDefaultAsync(cancellationToken);

                if (name is not null)
                {
                    return name;
                }

                logger.LogDebug(
                    "No agent definition found for {AgentId}; falling back to path.",
                    path);
                return path;
            }

            if (string.Equals(scheme, "unit", StringComparison.OrdinalIgnoreCase))
            {
                var name = await db.UnitDefinitions
                    .AsNoTracking()
                    .Where(u => u.UnitId == path && u.DeletedAt == null)
                    .Select(u => u.Name)
                    .FirstOrDefaultAsync(cancellationToken);

                if (name is not null)
                {
                    return name;
                }

                logger.LogDebug(
                    "No unit definition found for {UnitId}; falling back to path.",
                    path);
                return path;
            }

            // For human:// and any other scheme, fall back to the path component.
            // The human's display name is carried by the authentication claims, not
            // by a DB table in the OSS build; the path (user-id) is already readable.
            return path;
        }
        catch (Exception ex)
        {
            logger.LogDebug(
                ex,
                "Failed to resolve display name for {Address}; falling back to path.",
                address);
            return path;
        }
    }
}