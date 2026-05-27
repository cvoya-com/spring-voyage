// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Data.Entities;

using Cvoya.Spring.Core.Tenancy;

/// <summary>
/// Persisted message-history row. Implements the Thread Timeline write
/// contract from <see href="../../../docs/decisions/0030-thread-model.md">ADR-0030</see>
/// and the EF-authoritative ownership decision in
/// <see href="../../../docs/decisions/0040-actor-state-ownership-matrix.md">ADR-0040</see>:
/// every domain message produces exactly one row in this table at dispatch
/// time. Pre-ADR the platform reconstructed the timeline by parsing
/// <c>activity_events.Details</c> JSON; with this entity in place
/// <c>activity_events</c> becomes audit-only and the message-history reads
/// (issue #2054) move to indexed SQL.
/// </summary>
/// <remarks>
/// <para>
/// <b>Foreign key.</b> <see cref="ThreadId"/> references
/// <c>threads.id</c>; the thread row is allocated by
/// <see cref="Cvoya.Spring.Core.Messaging.IThreadRegistry"/> ahead of the
/// message write so the FK insert always succeeds for a well-formed
/// dispatch.
/// </para>
/// <para>
/// <b>Sender / recipient encoding.</b> The platform's wire address is a
/// <c>scheme:&lt;32-hex&gt;</c> tuple
/// (<see cref="Cvoya.Spring.Core.Messaging.Address"/>), and the EF row
/// stores the same shape as two columns rather than a single string —
/// indexing on the Guid id is cheaper, and downstream filters (find every
/// message a human sent, every message addressed to a unit, etc.) become
/// straight equality predicates on the typed column.
/// </para>
/// </remarks>
public class MessageEntity : ITenantScopedEntity
{
    /// <summary>
    /// Stable Guid identity of the message (primary key). Matches the
    /// id assigned by the API layer / origin actor — the same id appears
    /// on the corresponding <c>MessageArrived</c> activity event so
    /// observers can join the two records.
    /// </summary>
    public Guid Id { get; set; }

    /// <summary>Tenant that owns this message row.</summary>
    public Guid TenantId { get; set; }

    /// <summary>
    /// Foreign key to <c>threads.id</c>. Domain messages always carry a
    /// resolved thread id by the time they reach the dispatcher; the FK
    /// guarantees inserts only land for threads the registry has seen.
    /// </summary>
    public Guid ThreadId { get; set; }

    /// <summary>
    /// Sender address scheme (<c>agent</c>, <c>unit</c>, <c>human</c>,
    /// <c>connector</c>, …). Stored alongside <see cref="SenderId"/>
    /// so the (scheme, id) pair fully reconstructs the original
    /// <see cref="Cvoya.Spring.Core.Messaging.Address"/>.
    /// </summary>
    public string SenderScheme { get; set; } = string.Empty;

    /// <summary>Sender address Guid identity.</summary>
    public Guid SenderId { get; set; }

    /// <summary>Recipient address scheme. See <see cref="SenderScheme"/>.</summary>
    public string RecipientScheme { get; set; } = string.Empty;

    /// <summary>Recipient address Guid identity.</summary>
    public Guid RecipientId { get; set; }

    /// <summary>
    /// Message type (<c>Domain</c>, <c>Cancel</c>, <c>HealthCheck</c>, etc.).
    /// Stored as a string so adding a new
    /// <see cref="Cvoya.Spring.Core.Messaging.MessageType"/> member is
    /// additive at the wire / log level without a schema migration. Only
    /// <c>Domain</c> messages are persisted; control messages are
    /// runtime-only.
    /// </summary>
    public string MessageType { get; set; } = string.Empty;

    /// <summary>
    /// Extracted text body, when the canonical
    /// <see cref="Cvoya.Spring.Core.Messaging.Rendering.IMessagePayloadRendererRegistry"/>
    /// claims the payload shape (#2843). Null for structured / non-text
    /// payloads — readers fall back to <see cref="Payload"/>.
    /// </summary>
    public string? Body { get; set; }

    /// <summary>
    /// Raw <see cref="System.Text.Json.JsonElement"/> payload as it
    /// crossed the dispatcher, serialised to <c>jsonb</c>. Always populated
    /// (never null) so downstream readers can render structured replies
    /// without losing fidelity.
    /// </summary>
    public string Payload { get; set; } = "null";

    /// <summary>
    /// Wall-clock timestamp the dispatcher accepted the message. Sourced
    /// from <see cref="Cvoya.Spring.Core.Messaging.Message.Timestamp"/>
    /// so origin and persistence agree to the millisecond.
    /// </summary>
    public DateTimeOffset SentAt { get; set; }

    /// <summary>
    /// Optional retraction timestamp. Null on insert; populated when a
    /// follow-up retraction event is recorded (out of scope for #2053 —
    /// the column is reserved so the retraction issue can land without a
    /// follow-up migration).
    /// </summary>
    public DateTimeOffset? RetractedAt { get; set; }
}
