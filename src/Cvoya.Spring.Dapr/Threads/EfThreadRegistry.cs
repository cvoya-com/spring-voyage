// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Threads;

using System.Text.Json;

using Cvoya.Spring.Core.Identifiers;
using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Core.Tenancy;
using Cvoya.Spring.Dapr.Data;
using Cvoya.Spring.Dapr.Data.Entities;

using Microsoft.EntityFrameworkCore;

/// <summary>
/// EF Core-backed <see cref="IThreadRegistry"/>. Implements participant-set
/// identity per <see href="../../../docs/decisions/0030-thread-model.md">ADR-0030</see>:
/// the same participant set in any order resolves to the same thread id, and
/// concurrent inserts converge on a single row via the unique
/// <c>(tenant_id, participant_key)</c> index.
/// </summary>
/// <remarks>
/// <para>
/// <b>Canonicalisation.</b> Each <see cref="Address"/> is rendered to its
/// canonical wire form (<c>scheme:&lt;32-hex&gt;</c>), lower-cased, sorted, and
/// de-duplicated. The resulting list is joined with <c>|</c> to form the
/// deterministic participant key. The pipe separator never appears inside a
/// canonical address (which is <c>scheme:hex</c>) so the join is unambiguous.
/// </para>
/// <para>
/// <b>Concurrency.</b> Two callers racing to create the same thread will both
/// reach the insert path; the unique index promotes the loser's
/// <see cref="DbUpdateException"/> into a re-read of the winner's row. Both
/// callers see the same id.
/// </para>
/// </remarks>
public class EfThreadRegistry : IThreadRegistry
{
    private static readonly JsonSerializerOptions ParticipantsJson = new()
    {
        // Compact, no whitespace — same shape on read and write.
    };

    private readonly SpringDbContext _db;
    private readonly ITenantContext _tenantContext;

    /// <summary>Creates a new <see cref="EfThreadRegistry"/>.</summary>
    public EfThreadRegistry(SpringDbContext db, ITenantContext tenantContext)
    {
        _db = db;
        _tenantContext = tenantContext;
    }

    /// <inheritdoc />
    public async Task<string> GetOrCreateAsync(
        IEnumerable<Address> participants,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(participants);

        var canonical = Canonicalise(participants);
        if (canonical.Count == 0)
        {
            throw new ArgumentException(
                "Thread participants must contain at least one address.",
                nameof(participants));
        }

        var participantKey = string.Join('|', canonical);

        var existing = await _db.Threads
            .AsNoTracking()
            .FirstOrDefaultAsync(t => t.ParticipantKey == participantKey, cancellationToken);

        if (existing is not null)
        {
            return GuidFormatter.Format(existing.Id);
        }

        var now = DateTimeOffset.UtcNow;
        var entity = new ThreadEntity
        {
            Id = Guid.NewGuid(),
            TenantId = _tenantContext.CurrentTenantId,
            ParticipantKey = participantKey,
            Participants = JsonSerializer.Serialize(canonical, ParticipantsJson),
            CreatedAt = now,
            LastActivityAt = now,
            Status = "active",
        };

        try
        {
            _db.Threads.Add(entity);
            await _db.SaveChangesAsync(cancellationToken);
            return GuidFormatter.Format(entity.Id);
        }
        catch (DbUpdateException)
        {
            // Race: another caller inserted the row. Detach the conflicting
            // entry so the context is clean, then re-read the winner.
            _db.Entry(entity).State = EntityState.Detached;

            var winner = await _db.Threads
                .AsNoTracking()
                .FirstOrDefaultAsync(t => t.ParticipantKey == participantKey, cancellationToken);

            if (winner is not null)
            {
                return GuidFormatter.Format(winner.Id);
            }

            // Should never happen — the unique index guarantees a row exists.
            throw;
        }
    }

    /// <inheritdoc />
    public async Task<ThreadRegistryEntry?> ResolveAsync(
        string threadId,
        CancellationToken cancellationToken = default)
    {
        if (!GuidFormatter.TryParse(threadId, out var parsed))
        {
            return null;
        }

        var row = await _db.Threads
            .AsNoTracking()
            .FirstOrDefaultAsync(t => t.Id == parsed, cancellationToken);

        if (row is null)
        {
            return null;
        }

        var participants = DeserialiseParticipants(row.Participants);
        return new ThreadRegistryEntry(
            ThreadId: GuidFormatter.Format(row.Id),
            Participants: participants,
            CreatedAt: row.CreatedAt);
    }

    /// <summary>
    /// Renders the input addresses to their canonical lower-case wire form,
    /// removes duplicates, and returns them in stable sort order so the
    /// participant key is independent of input ordering.
    /// </summary>
    private static IReadOnlyList<string> Canonicalise(IEnumerable<Address> participants)
    {
        var seen = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var address in participants)
        {
            if (address is null)
            {
                continue;
            }

            // Address.ToString() already emits scheme:<32-hex-no-dash> in lower
            // case (GuidFormatter.Format uses "N"). Normalise the scheme too in
            // case a caller built the Address with a mixed-case scheme literal.
            var canonical = $"{address.Scheme.ToLowerInvariant()}:{GuidFormatter.Format(address.Id)}";
            seen.Add(canonical);
        }

        return seen.ToList();
    }

    private static IReadOnlyList<Address> DeserialiseParticipants(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return Array.Empty<Address>();
        }

        try
        {
            var raw = JsonSerializer.Deserialize<string[]>(json, ParticipantsJson);
            if (raw is null)
            {
                return Array.Empty<Address>();
            }

            var addresses = new List<Address>(raw.Length);
            foreach (var entry in raw)
            {
                if (Address.TryParse(entry, out var address) && address is not null)
                {
                    addresses.Add(address);
                }
            }

            return addresses;
        }
        catch (JsonException)
        {
            return Array.Empty<Address>();
        }
    }
}
