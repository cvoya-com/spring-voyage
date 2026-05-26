// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Api.Models;

using System.Text.Json;

using Cvoya.Spring.Core.Observability;

/// <summary>
/// Query-string binding for <c>GET /api/v1/threads</c>. Mirrors
/// <see cref="ThreadQueryFilters"/> on the wire; kept as an API-layer
/// DTO so the Core model can evolve independently.
/// </summary>
/// <param name="Unit">Optional unit-name filter.</param>
/// <param name="Agent">Optional agent-name filter.</param>
/// <param name="Participant">Optional <c>scheme://path</c> participant filter.</param>
/// <param name="Limit">Optional row cap (default 50).</param>
/// <param name="Archived">
/// Optional archive-state filter (#2732). Omitted or <c>false</c>
/// excludes archived (fully-orphaned) threads from the response — the
/// default engagement list keeps the live list uncluttered.
/// <c>true</c> returns ONLY archived threads — the portal's separate
/// archive surface uses this. An explicit <c>null</c> is treated as
/// omitted (defensive — same as the default).
/// </param>
public record ThreadListQuery(
    string? Unit,
    string? Agent,
    string? Participant,
    int? Limit,
    bool? Archived = null);

/// <summary>
/// Query-string binding for <c>GET /api/v1/tenant/observation/threads</c>
/// (#2787 / #2790). Extends the engagement-side <see cref="ThreadListQuery"/>
/// shape with optional <see cref="Search"/> and <see cref="Since"/>
/// filters that drive the Conversations view's filter bar + the
/// <c>spring conversations list</c> CLI flags. Kept distinct from
/// <see cref="ThreadListQuery"/> so the participant-scoped endpoint's
/// API surface stays stable.
/// </summary>
/// <param name="Unit">Narrow to threads involving this unit.</param>
/// <param name="Agent">Narrow to threads involving this agent.</param>
/// <param name="Participant">Narrow to threads where the given canonical address appears.</param>
/// <param name="Limit">Optional row cap (default 50).</param>
/// <param name="Archived">
/// Archive-state filter. <c>null</c> / <c>false</c> excludes archived
/// threads (default); <c>true</c> returns only archived threads.
/// </param>
/// <param name="Search">
/// Optional case-insensitive substring filter applied to the thread
/// summary text, each participant's display name, and the canonical
/// address. Implemented at the API host layer because participant
/// display-name resolution happens after the query service returns.
/// </param>
/// <param name="Since">
/// Optional lower bound on <c>LastActivity</c>. Accepts any ISO-8601
/// timestamp (e.g. <c>2026-05-25</c> or <c>2026-05-25T00:00:00Z</c>);
/// threads with last activity before this instant are excluded.
/// </param>
public record ObservationListQuery(
    string? Unit,
    string? Agent,
    string? Participant,
    int? Limit,
    bool? Archived = null,
    string? Search = null,
    DateTimeOffset? Since = null);

/// <summary>
/// Semantic kind of a thread message. The discriminator is convention-driven —
/// the platform accepts and persists the value without enforcing it; units and
/// agents are expected to set the appropriate kind when sending.
/// </summary>
/// <remarks>
/// <list type="bullet">
///   <item>
///     <term>information</term>
///     <description>Default. A regular informational message — status updates, progress reports, results.</description>
///   </item>
///   <item>
///     <term>question</term>
///     <description>A unit or agent is asking the human (or another participant) a clarifying question.</description>
///   </item>
///   <item>
///     <term>answer</term>
///     <description>A human or agent is replying to a clarifying question. Set by <c>engagement answer</c>.</description>
///   </item>
///   <item>
///     <term>error</term>
///     <description>The sender encountered an error and is surfacing it on the thread for visibility.</description>
///   </item>
/// </list>
/// </remarks>
public static class MessageKind
{
    /// <summary>Default kind — a regular informational message.</summary>
    public const string Information = "information";

    /// <summary>A unit or agent is asking for clarification.</summary>
    public const string Question = "question";

    /// <summary>A reply to a clarifying question.</summary>
    public const string Answer = "answer";

    /// <summary>An error surfaced on the thread for visibility.</summary>
    public const string Error = "error";
}

/// <summary>
/// Request body for <c>POST /api/v1/threads/{id}/messages</c>. A thin
/// wrapper over <see cref="SendMessageRequest"/> — the thread id comes
/// from the path so callers don't repeat it in the body.
/// </summary>
/// <param name="To">Destination address. Same shape as <see cref="SendMessageRequest.To"/>.</param>
/// <param name="Text">Free-text message body; wrapped in a <c>Domain</c> payload server-side.</param>
/// <param name="Kind">
/// Optional semantic kind of this message. Defaults to <see cref="MessageKind.Information"/>
/// when omitted. See <see cref="MessageKind"/> for the full value set.
/// </param>
public record ThreadMessageRequest(
    AddressDto To,
    string Text,
    string? Kind = null);

/// <summary>
/// Response body for <c>POST /api/v1/threads/{id}/messages</c>.
/// </summary>
/// <param name="MessageId">The generated message id.</param>
/// <param name="ThreadId">The thread the message was sent into.</param>
/// <param name="ResponsePayload">The response payload from the target, if any.</param>
/// <param name="Kind">
/// The semantic kind that was accepted and persisted for this message.
/// Echoes the value from the request (or <see cref="MessageKind.Information"/> when
/// the request omitted it).
/// </param>
public record ThreadMessageResponse(
    Guid MessageId,
    string ThreadId,
    JsonElement? ResponsePayload,
    string Kind = MessageKind.Information);
