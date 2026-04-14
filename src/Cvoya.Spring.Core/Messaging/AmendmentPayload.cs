// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Messaging;

/// <summary>
/// Body of a mid-flight amendment message (<see cref="MessageType.Amendment"/>).
/// See #142.
/// </summary>
/// <remarks>
/// <para>
/// Amendments reuse the existing <see cref="Message"/> envelope — we add a
/// discriminator via <see cref="MessageType.Amendment"/> rather than a new
/// <c>amendment://</c> address scheme, so routing, addressing, and auditing
/// stay on one code path. The amendment's <see cref="Message.Payload"/>
/// carries a JSON-serialized <see cref="AmendmentPayload"/>.
/// </para>
/// <para>
/// <see cref="Text"/> is free-form guidance ("rebase before pushing", "stop
/// after the current tool call"). <see cref="Priority"/> controls how quickly
/// the recipient reacts. <see cref="CorrelationId"/> threads the amendment
/// back to the live turn it is adjusting — typically the conversation id the
/// turn was started under — so operators can align amendments with the turn
/// they amended in audit views.
/// </para>
/// <para>
/// <see cref="Text"/> and <see cref="Priority"/> are the only fields the
/// recipient strictly requires; the others are advisory. A deserialized
/// payload with a blank <see cref="Text"/> is treated as malformed and
/// rejected.
/// </para>
/// </remarks>
/// <param name="Text">Free-form instruction body for the live turn.</param>
/// <param name="Priority">How urgently the amendment must be honoured.</param>
/// <param name="CorrelationId">
/// Optional identifier threading this amendment back to the live turn being
/// amended. Typically the conversation id; defaults to <c>null</c> when the
/// sender does not provide one.
/// </param>
public record AmendmentPayload(
    string Text,
    AmendmentPriority Priority = AmendmentPriority.Informational,
    string? CorrelationId = null);