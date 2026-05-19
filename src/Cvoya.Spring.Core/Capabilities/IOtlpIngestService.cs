// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Capabilities;

using System.Text.Json;

using Cvoya.Spring.Core.Messaging;

/// <summary>
/// Ingest contract for OTLP/HTTP+JSON span and log batches. Issue #2492.
/// </summary>
/// <remarks>
/// <para>
/// The HTTP endpoint deserialises the OTLP JSON wire form into typed
/// runtime DTOs, then hands a list of <see cref="OtlpEventIngest"/>
/// records to <see cref="IngestAsync"/>. The ingest service applies
/// redaction, capture-level truncation, and rate limiting, then
/// publishes each event onto the shared <see cref="IActivityEventBus"/>
/// — at which point the existing persister and SSE relay carry it the
/// rest of the way.
/// </para>
/// <para>
/// The interface lives in <c>Cvoya.Spring.Core</c> so cloud overlays
/// (forwarding to external OTel backends like Datadog or Tempo) can
/// register a decorating implementation without taking a dependency on
/// the Dapr persistence layer. The OSS implementation is the only one
/// in this repo today.
/// </para>
/// </remarks>
public interface IOtlpIngestService
{
    /// <summary>
    /// Best-effort ingest of a batch of normalised OTel events. Failures
    /// (auth, payload validation, downstream publish) are caught
    /// internally; this method does not throw. Callers MUST treat the
    /// returned result as an advisory diagnostic only — the A2A
    /// request/response path must not block on capture.
    /// </summary>
    /// <param name="events">The batch to ingest.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>
    /// A diagnostic result counting accepted / dropped events for the
    /// ingest endpoint's response shape.
    /// </returns>
    Task<OtlpIngestResult> IngestAsync(
        IReadOnlyList<OtlpEventIngest> events,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Normalised OTel event handed to <see cref="IOtlpIngestService"/>.
/// One record per span, span-event, or log record. The kind hints what
/// the ingest pipeline does — span / log / progress / llm-turn /
/// tool-call — and the platform <see cref="ActivityEventType"/> is
/// derived from it server-side.
/// </summary>
/// <param name="Kind">The OTel event kind.</param>
/// <param name="Subject">
/// The subject the event is scoped to. Resolved from the OTel resource
/// attributes <c>sv.subject.uuid</c> + <c>sv.subject.kind</c>.
/// </param>
/// <param name="TenantId">
/// Tenant the event belongs to. Resolved from the <c>sv.tenant.id</c>
/// resource attribute (must match the caller's authenticated tenant —
/// the endpoint rejects mismatches before this method is called).
/// </param>
/// <param name="ThreadId">
/// Optional thread id from the <c>sv.thread.id</c> attribute; carried as
/// <see cref="ActivityEvent.CorrelationId"/>.
/// </param>
/// <param name="MessageId">Optional message id from <c>sv.message.id</c>.</param>
/// <param name="Timestamp">When the underlying span / log was observed.</param>
/// <param name="Summary">
/// A short human-readable summary. Truncation rules are applied to the
/// full payload in <see cref="Details"/>, but the summary stays
/// untouched so the SSE / portal feed has a stable one-liner.
/// </param>
/// <param name="Severity">
/// Severity mapped from OTel <c>severityNumber</c> / <c>severityText</c>
/// for logs, or inferred from span status for spans.
/// </param>
/// <param name="Details">
/// Full structured payload. Subject to redaction and capture-level
/// truncation server-side at ingest before persistence.
/// </param>
public sealed record OtlpEventIngest(
    OtlpEventKind Kind,
    Address Subject,
    Guid TenantId,
    string? ThreadId,
    string? MessageId,
    DateTimeOffset Timestamp,
    string Summary,
    ActivitySeverity Severity,
    JsonElement Details);

/// <summary>
/// Result of a single ingest batch. Counts are diagnostic — callers
/// MUST treat the result as advisory; OTLP itself doesn't require
/// per-event acknowledgement.
/// </summary>
/// <param name="Accepted">Events written to the activity bus.</param>
/// <param name="DroppedCapture">Events dropped because the tenant is at <c>off</c>.</param>
/// <param name="DroppedRate">Events dropped because the rate limiter rejected them.</param>
/// <param name="DroppedError">Events dropped because publishing threw.</param>
public sealed record OtlpIngestResult(
    int Accepted,
    int DroppedCapture,
    int DroppedRate,
    int DroppedError);

/// <summary>
/// What kind of OTel event a normalised <see cref="OtlpEventIngest"/>
/// represents. Maps 1:1 onto an <see cref="ActivityEventType"/> at
/// ingest time.
/// </summary>
public enum OtlpEventKind
{
    /// <summary>A span emitted by the runtime container.</summary>
    Span,

    /// <summary>A log record emitted by the runtime container.</summary>
    Log,

    /// <summary>An <c>sv.progress</c> span event with a free-text message.</summary>
    Progress,

    /// <summary>A full LLM turn (<c>sv.llm.turn</c> span).</summary>
    LlmTurn,

    /// <summary>A tool call (<c>sv.tool.call</c> span).</summary>
    ToolCall,
}
