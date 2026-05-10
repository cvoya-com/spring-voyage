// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Threads;

using System.Text.Json;

using Cvoya.Spring.Core.Identifiers;
using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Core.Tenancy;
using Cvoya.Spring.Dapr.Actors;
using Cvoya.Spring.Dapr.Data;
using Cvoya.Spring.Dapr.Data.Entities;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

/// <summary>
/// EF Core-backed <see cref="IMessageWriter"/>. Inserts a
/// <see cref="MessageEntity"/> for every Domain message accepted by the
/// dispatcher and bumps the parent thread's <c>last_activity_at</c> in the
/// same <see cref="DbContext.SaveChangesAsync(CancellationToken)"/> call.
/// </summary>
/// <remarks>
/// <para>
/// <b>Idempotency.</b> Re-dispatch (e.g. retry after a transient failure
/// downstream) hits the existence check first and short-circuits without
/// touching the row. The primary-key uniqueness on <c>messages.id</c> is the
/// final guard — concurrent writers race the existence check, the loser
/// gets a <see cref="DbUpdateException"/>, and the writer treats that as a
/// no-op rather than surfacing it.
/// </para>
/// <para>
/// <b>Failure model.</b> A persistence failure here is intentionally
/// surfaced — the dispatcher must see it so the API surface returns 5xx
/// rather than silently delivering an unrecorded message. ADR-0040 makes
/// the <c>messages</c> table authoritative for thread history; treating
/// the write as best-effort would let dispatch silently diverge from the
/// timeline. <see cref="Cvoya.Spring.Dapr.Routing.MessageRouter"/>
/// propagates the exception unchanged.
/// </para>
/// </remarks>
public class EfMessageWriter(
    SpringDbContext db,
    ITenantContext tenantContext,
    ILoggerFactory loggerFactory) : IMessageWriter
{
    private static readonly JsonSerializerOptions PayloadJson = new()
    {
        // Compact: no whitespace inside the persisted jsonb column.
    };

    private readonly ILogger _logger = loggerFactory.CreateLogger<EfMessageWriter>();

    /// <inheritdoc />
    public async Task WriteAsync(Message message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);

        if (!IMessageWriter.ShouldWrite(message))
        {
            return;
        }

        if (!GuidFormatter.TryParse(message.ThreadId!, out var threadGuid))
        {
            // Belt-and-braces: ShouldWrite already filtered this case. Log and
            // skip rather than throw — the API path validates the thread id
            // before calling, and the dispatcher only writes for accepted
            // messages.
            _logger.LogWarning(
                "Skipping message persistence for {MessageId}: thread id '{ThreadId}' is not a valid Guid.",
                message.Id, message.ThreadId);
            return;
        }

        var existing = await db.Messages
            .AsNoTracking()
            .AnyAsync(m => m.Id == message.Id, cancellationToken);

        if (existing)
        {
            // Re-dispatch path — the write already landed for this id.
            return;
        }

        var entity = new MessageEntity
        {
            Id = message.Id,
            TenantId = tenantContext.CurrentTenantId,
            ThreadId = threadGuid,
            SenderScheme = message.From.Scheme,
            SenderId = message.From.Id,
            RecipientScheme = message.To.Scheme,
            RecipientId = message.To.Id,
            MessageType = message.Type.ToString(),
            Body = MessageReceivedDetails.TryExtractText(message.Payload),
            Payload = SerialisePayload(message.Payload),
            SentAt = message.Timestamp,
            RetractedAt = null,
        };

        db.Messages.Add(entity);

        // Bump the parent thread's last_activity_at so inbox-style listings
        // surface the most recently active threads first. The row already
        // exists by the time we get here — the API path resolves it through
        // IThreadRegistry.GetOrCreateAsync before the dispatch — so a
        // straightforward UPDATE is enough; we do not re-insert.
        var thread = await db.Threads
            .FirstOrDefaultAsync(t => t.Id == threadGuid, cancellationToken);

        if (thread is not null)
        {
            // Only move forward — concurrent dispatches can land out of
            // wall-clock order; the surface wants the most recent activity,
            // not the most recently-saved row.
            if (thread.LastActivityAt < message.Timestamp)
            {
                thread.LastActivityAt = message.Timestamp;
            }
        }
        else
        {
            // The FK insert below will fail; log here so the failure mode is
            // grep-able in the dispatcher logs.
            _logger.LogWarning(
                "Message {MessageId} references unknown thread {ThreadId}; FK insert will fail.",
                message.Id, message.ThreadId);
        }

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (IsPrimaryKeyViolation(ex))
        {
            // Concurrent writer landed the same id first. Detach the
            // conflicting entry so the context is clean and treat the call
            // as a no-op — the row exists, which is the post-condition the
            // caller cares about.
            db.Entry(entity).State = EntityState.Detached;
            _logger.LogDebug(
                "Message {MessageId} was inserted concurrently; treating WriteAsync as a no-op.",
                message.Id);
        }
    }

    private static string SerialisePayload(JsonElement payload)
    {
        // Control messages (Cancel, HealthCheck, …) can carry an undefined
        // JsonElement; the column is non-null so we persist a JSON null
        // rather than failing the insert.
        if (payload.ValueKind == JsonValueKind.Undefined)
        {
            return "null";
        }

        return JsonSerializer.Serialize(payload, PayloadJson);
    }

    /// <summary>
    /// Heuristic check for "the unique-PK on messages.id was violated."
    /// EF does not expose a portable error code for the case, so we look
    /// for the well-known pieces of the inner-exception message that both
    /// the in-memory and Postgres providers emit.
    /// </summary>
    private static bool IsPrimaryKeyViolation(DbUpdateException ex)
    {
        var inner = ex.InnerException?.Message ?? ex.Message;
        // Postgres: "23505: duplicate key value violates unique constraint".
        // InMemory provider: "An item with the same key has already been added".
        return inner.Contains("23505", StringComparison.Ordinal)
            || inner.Contains("duplicate key", StringComparison.OrdinalIgnoreCase)
            || inner.Contains("same key", StringComparison.OrdinalIgnoreCase);
    }
}
