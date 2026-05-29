// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connector.Slack.Commands;

using System.Collections.Generic;
using System.Text.Json;

/// <summary>
/// Handles the three slash commands per ADR-0061 §5:
/// </summary>
/// <list type="bullet">
///   <item><description>
///     <c>/sv-thread</c> — opens a Block Kit modal whose multi-select
///     is populated from the SV directory (agents, units, SV humans
///     other than the bound user's primary).
///   </description></item>
///   <item><description>
///     <c>/sv-threads</c> — opens a modal listing the bound user's
///     active SV threads with deep links to each parent message.
///   </description></item>
///   <item><description>
///     <c>/sv-help</c> — posts a static cheat-sheet response.
///   </description></item>
/// </list>
/// <para>
/// Non-DM invocations of any of the three commands respond with the
/// same DM-only refusal message used by the unbound-user path.
/// </para>
public interface ISlackCommandDispatcher
{
    /// <summary>
    /// Dispatches a slash-command invocation. The handler does most
    /// of its work synchronously (Slack enforces a 3-second handshake
    /// budget on the response), but the actual modal-open call is
    /// non-blocking — the response body is what Slack uses to
    /// acknowledge the command.
    /// </summary>
    /// <param name="form">
    /// The parsed form-encoded fields Slack delivers (command,
    /// trigger_id, channel_id, channel_name, user_id, team_id,
    /// text, response_url).
    /// </param>
    /// <param name="cancellationToken">Propagates cancellation.</param>
    Task<SlackCommandDispatchOutcome> DispatchAsync(
        IReadOnlyDictionary<string, string> form,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Dispatches a Block Kit interaction payload (modal submit,
    /// button click, etc.). The slash-command handler relies on this
    /// for the <c>view_submission</c> follow-up after the user fills
    /// in the <c>/sv-thread</c> modal.
    /// <para>
    /// Slack imposes a 3-second budget on the HTTP response to a
    /// <c>view_submission</c> payload (#2879). The dispatcher therefore
    /// runs only the synchronous validation work on the request thread
    /// — payload parse, binding/bound-user lookup, inline-error
    /// detection — and fires the actual thread-creation work
    /// (DM open, <c>chat.postMessage</c>, persistence, optional
    /// initial-message routing) on a background task. The returned
    /// <see cref="SlackInteractionResponse"/> carries an optional JSON
    /// body the endpoint serialises back to Slack — set for
    /// <c>response_action: errors</c> inline-validation failures,
    /// <c>null</c> for the happy-path ack.
    /// </para>
    /// </summary>
    Task<SlackInteractionResponse> DispatchInteractionAsync(
        JsonElement payload,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Outcome of a slash-command dispatch.
/// </summary>
public enum SlackCommandDispatchOutcome
{
    /// <summary>The command was processed.</summary>
    Handled,

    /// <summary>The command's slug was not one this connector serves.</summary>
    UnknownCommand,

    /// <summary>
    /// The command's <c>team_id</c> did not match a known tenant
    /// binding. Returns the same response shape as an unbound user
    /// per ADR-0061 §2.4.
    /// </summary>
    UnknownTeam,
}

/// <summary>
/// Result of dispatching a Slack Block Kit interaction payload. The
/// endpoint maps <see cref="ResponseBody"/> directly onto the HTTP
/// response body: when set, the body is serialised as JSON (Slack
/// interprets <c>response_action: errors</c> / <c>clear</c> /
/// <c>update</c> / <c>push</c>); when <c>null</c>, the endpoint
/// returns an empty <c>200 OK</c>, which Slack treats as a plain ack
/// and closes the modal.
/// </summary>
/// <param name="Outcome">High-level dispatch classification.</param>
/// <param name="ResponseBody">
/// Optional JSON-serialisable body to return to Slack. <c>null</c>
/// for the happy-path ack (modal closes). For inline-validation
/// failures, set to an object shaped
/// <c>{ response_action: "errors", errors: { &lt;block_id&gt;: "..." } }</c>
/// so the modal stays open with the field error highlighted.
/// </param>
public sealed record SlackInteractionResponse(
    SlackCommandDispatchOutcome Outcome,
    object? ResponseBody = null);
