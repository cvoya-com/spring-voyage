// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Skills;

/// <summary>
/// Per-call caller context threaded into <see cref="ISkillRegistry.InvokeAsync(string, System.Text.Json.JsonElement, ToolCallContext, System.Threading.CancellationToken)"/>
/// so a tool implementation can answer questions about WHO is calling it
/// (e.g. "list members of *my* unit", "what is *my* expertise"). The MCP
/// server resolves these from the active session before invoking the
/// registry. Tools that don't need caller info ignore the parameter — the
/// default implementation on <see cref="ISkillRegistry"/> drops it for
/// backwards-compatibility with skills authored against the original
/// no-context overload.
/// </summary>
/// <param name="CallerId">
/// The caller's stable Guid identifier as a no-dash 32-char hex string
/// (Spring Voyage canonical Guid form, see <c>Cvoya.Spring.Core.Identifiers.GuidFormatter</c>).
/// Matches the actor id of the calling agent or unit.
/// </param>
/// <param name="CallerKind">
/// The caller's kind — either <c>"agent"</c> or <c>"unit"</c>. Carries
/// the same value as the corresponding scheme constant on
/// <see cref="Cvoya.Spring.Core.Messaging.Address"/>.
/// </param>
/// <param name="ThreadId">
/// The thread the caller is currently serving. Useful for tools that scope
/// their behaviour to the current conversation.
/// </param>
/// <param name="MessageId">
/// The inbound message the current turn is responding to. Carried so a
/// messaging tool (<c>sv.messaging.send</c> / <c>sv.messaging.broadcast</c>)
/// can stamp the outgoing <see cref="Cvoya.Spring.Core.Messaging.Message"/>
/// and any audit record with the cause of the turn — the per-turn delivery
/// authority the retired callback JWT used to carry (ADR-0051). The MCP
/// server materialises it from the active session. Defaults to
/// <see cref="System.Guid.Empty"/> so context construction in tests written
/// before ADR-0051 keeps compiling.
/// </param>
public sealed record ToolCallContext(
    string CallerId,
    string CallerKind,
    string ThreadId,
    Guid MessageId = default);
