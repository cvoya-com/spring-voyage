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
/// Request body for <c>POST /api/v1/threads/{id}/messages</c>. A thin
/// wrapper over <see cref="SendMessageRequest"/> — the thread id comes
/// from the path so callers don't repeat it in the body.
/// </summary>
/// <param name="To">Destination address. Same shape as <see cref="SendMessageRequest.To"/>.</param>
/// <param name="Text">Free-text message body; wrapped in a <c>Domain</c> payload server-side.</param>
public record ThreadMessageRequest(
    AddressDto To,
    string Text);

/// <summary>
/// Response body for <c>POST /api/v1/threads/{id}/messages</c>.
/// </summary>
/// <param name="MessageId">The generated message id.</param>
/// <param name="ThreadId">The thread the message was sent into.</param>
/// <param name="ResponsePayload">The response payload from the target, if any.</param>
public record ThreadMessageResponse(
    Guid MessageId,
    string ThreadId,
    JsonElement? ResponsePayload);

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