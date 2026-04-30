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
/// <param name="Status">Optional status filter (<c>active</c> / <c>completed</c>).</param>
/// <param name="Participant">Optional <c>scheme://path</c> participant filter.</param>
/// <param name="Limit">Optional row cap (default 50).</param>
public record ThreadListQuery(
    string? Unit,
    string? Agent,
    string? Status,
    string? Participant,
    int? Limit);

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

/// <summary>
/// Request body for <c>POST /api/v1/threads/{id}/close</c> (#1038). The
/// reason is optional — when supplied it surfaces on the
/// <c>ThreadClosed</c> activity event the actor emits, so operators can
/// see <em>why</em> a thread was aborted (operator request, runaway tool,
/// upstream incident, etc.). Present as a body rather than a query string so
/// long reasons aren't truncated by URL length limits and aren't logged in
/// server access logs.
/// </summary>
/// <param name="Reason">Optional human-readable reason for closing.</param>
public record CloseThreadRequest(string? Reason);