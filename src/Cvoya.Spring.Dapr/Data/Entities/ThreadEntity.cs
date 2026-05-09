// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Data.Entities;

using Cvoya.Spring.Core.Tenancy;

/// <summary>
/// Persisted thread-registry row. Each row maps a deterministic
/// <see cref="ParticipantKey"/> (canonicalised participant set, sorted and
/// joined) to a stable thread <see cref="Id"/>. Implements the participant-set
/// identity model from ADR-0030 and lives in EF per ADR-0040 (actor-state
/// ownership matrix — thread identity is configuration-shaped data).
/// </summary>
public class ThreadEntity : ITenantScopedEntity
{
    /// <summary>Stable Guid identity of the thread (primary key).</summary>
    public Guid Id { get; set; }

    /// <summary>Tenant that owns the thread row.</summary>
    public Guid TenantId { get; set; }

    /// <summary>
    /// Deterministic key derived from the canonicalised participant set:
    /// lower-cased canonical address strings (<c>scheme:&lt;32-hex&gt;</c>),
    /// sorted, de-duplicated, joined with <c>|</c>. Two messages whose
    /// participant sets differ only by ordering or duplicates collapse to the
    /// same key. Backed by a unique index on <c>(tenant_id, participant_key)</c>.
    /// </summary>
    public string ParticipantKey { get; set; } = string.Empty;

    /// <summary>
    /// JSON-encoded array of canonical participant address strings, in the
    /// canonical sort order used to build <see cref="ParticipantKey"/>.
    /// Stored as <c>jsonb</c> on PostgreSQL.
    /// </summary>
    public string Participants { get; set; } = "[]";

    /// <summary>Timestamp when the thread row was first inserted.</summary>
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>
    /// Last time activity was observed on the thread. Defaults to
    /// <see cref="CreatedAt"/> on insert; lifecycle services bump this as
    /// messages arrive.
    /// </summary>
    public DateTimeOffset LastActivityAt { get; set; }

    /// <summary>
    /// Lifecycle status — <c>active</c> on insert; transitions to
    /// <c>completed</c> when a terminal event is observed. Stored as a string
    /// so the column is forwards-compatible with future statuses without DDL.
    /// </summary>
    public string Status { get; set; } = "active";
}
