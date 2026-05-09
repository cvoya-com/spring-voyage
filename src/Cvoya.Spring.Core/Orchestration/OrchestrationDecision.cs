// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Orchestration;

using System.Text.Json;

using Cvoya.Spring.Core.Messaging;

/// <summary>
/// First-class evidence record emitted whenever a unit's runtime invokes
/// one of the platform-supplied orchestration tools (see ADR-0039 § 4).
/// Persisted on the activity stream as
/// <c>Activity_OrchestrationDecision</c> rows so operators have a uniform
/// audit trail of every delegation, fan-out, and inspection across
/// every runtime image.
/// </summary>
/// <remarks>
/// <para>
/// <b>Reason</b> carries the runtime-supplied rationale as plain text —
/// what the agent's tool call passed in its <c>reason</c> argument. It
/// is <b>never</b> the model's hidden chain-of-thought; runtimes that
/// surface internal reasoning must redact it before the platform
/// observes the tool call.
/// </para>
/// <para>
/// This record is purely additive in this PR: no producers, no
/// consumers. Event emission lands later in the ADR-0039 execution
/// plan.
/// </para>
/// </remarks>
/// <param name="DecisionId">Stable identifier for this decision row.</param>
/// <param name="TenantId">Tenant that owns the unit producing the decision.</param>
/// <param name="UnitAddress">Address of the unit whose runtime made the decision.</param>
/// <param name="ThreadId">Conversation thread the decision belongs to.</param>
/// <param name="InputMessageId">Identifier of the inbound message that triggered the decision.</param>
/// <param name="Kind">The kind of decision (delegate, fan-out, inspect, no-op).</param>
/// <param name="Targets">Addresses the decision targets (one for delegate, many for fan-out, may be empty for inspect / no-op).</param>
/// <param name="Status">Outcome of the decision (accepted, routed, failed).</param>
/// <param name="ResultMessageIds">Identifiers of the messages produced by the decision, in target order.</param>
/// <param name="Reason">Runtime-supplied textual rationale; never hidden chain-of-thought.</param>
/// <param name="Metadata">Optional structured metadata captured alongside the decision.</param>
/// <param name="CreatedAt">UTC timestamp at which the platform recorded the decision.</param>
public sealed record OrchestrationDecision(
    Guid DecisionId,
    Guid TenantId,
    Address UnitAddress,
    Guid ThreadId,
    Guid InputMessageId,
    OrchestrationDecisionKind Kind,
    Address[] Targets,
    OrchestrationDecisionStatus Status,
    Guid[] ResultMessageIds,
    string? Reason,
    JsonElement? Metadata,
    DateTimeOffset CreatedAt);
