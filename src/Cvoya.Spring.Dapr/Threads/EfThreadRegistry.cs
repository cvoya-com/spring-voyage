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
/// <b>Concurrency.</b> Two callers racing to create the same thread both reach
/// the insert path. On Postgres the insert is
/// <c>ON CONFLICT (tenant_id, participant_key) DO NOTHING</c>, so the loser's
/// duplicate is swallowed in the database — no exception, and therefore no
/// fail:-level log noise (#3066) — and both callers re-read and converge on
/// the winning row's id. The in-memory test provider, which has no
/// <c>ON CONFLICT</c> support, keeps the Add + <see cref="DbUpdateException"/>
/// catch + re-read path to the same effect.
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
        };

        if (_db.Database.IsNpgsql())
        {
            // Postgres path: insert idempotently with
            // ON CONFLICT (tenant_id, participant_key) DO NOTHING so the
            // benign get-or-create race (overlapping participant sets racing
            // to first-contact, #3066) resolves in the database. The previous
            // Add + SaveChanges let the loser's SaveChanges throw a
            // DbUpdateException, which EF logs at fail: level (the 23505
            // duplicate-key noise on ux_threads_tenant_participant_key) before
            // our catch swallowed it. Mirrors the messages-table treatment
            // (#3056). Re-read by participant_key afterwards so we return the
            // winner's id whether this call or a racer inserted the row.
            await InsertThreadOnConflictAsync(entity, cancellationToken);

            var winner = await _db.Threads
                .AsNoTracking()
                .FirstOrDefaultAsync(t => t.ParticipantKey == participantKey, cancellationToken);

            if (winner is not null)
            {
                return GuidFormatter.Format(winner.Id);
            }

            // Should never happen — the insert either landed our row or a
            // concurrent winner's row, so a row with this key must exist.
            throw new InvalidOperationException(
                $"Thread row for participant key '{participantKey}' vanished after an " +
                "ON CONFLICT insert; expected the winning row to be readable.");
        }

        // Non-Npgsql providers (the in-memory test database) have no
        // ON CONFLICT support, so keep the Add + SaveChanges + unique-violation
        // swallow. Test writes are sequential, so the concurrency race the
        // Postgres path guards against does not arise here.
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

    /// <summary>
    /// Inserts the thread row with
    /// <c>ON CONFLICT (tenant_id, participant_key) DO NOTHING</c> so a
    /// concurrent duplicate is swallowed by the database rather than surfaced
    /// as a thrown <see cref="DbUpdateException"/> (which EF logs at fail:
    /// level, #3066). Postgres-only — the column list mirrors the
    /// <c>spring.threads</c> table; the interpolated values bind as
    /// parameters, with the two jsonb columns cast explicitly.
    /// <c>participant_name_snapshots</c> is seeded with the entity's <c>{}</c>
    /// default so a fresh row matches the EF write path.
    /// </summary>
    private async Task InsertThreadOnConflictAsync(
        ThreadEntity entity,
        CancellationToken cancellationToken)
    {
        await _db.Database.ExecuteSqlInterpolatedAsync(
            $"""
            INSERT INTO spring.threads
                (id, tenant_id, participant_key, participants,
                 participant_name_snapshots, created_at, last_activity_at)
            VALUES
                ({entity.Id}, {entity.TenantId}, {entity.ParticipantKey}, {entity.Participants}::jsonb,
                 {entity.ParticipantNameSnapshots}::jsonb, {entity.CreatedAt}, {entity.LastActivityAt})
            ON CONFLICT (tenant_id, participant_key) DO NOTHING
            """,
            cancellationToken);
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
