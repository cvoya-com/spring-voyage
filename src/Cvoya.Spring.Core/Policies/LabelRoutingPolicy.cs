// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Policies;

/// <summary>
/// Maps inbound-message labels onto unit members so a
/// <c>LabelRoutedOrchestrationStrategy</c> can dispatch work by what a human
/// has tagged rather than by LLM classification. Sixth concrete
/// <see cref="UnitPolicy"/> dimension — see #389.
/// </summary>
/// <remarks>
/// <para>
/// Matching is a <strong>case-insensitive set intersection</strong>: for a
/// given message, every label present in the payload is looked up in
/// <paramref name="TriggerLabels"/>; the first hit (iterating the message's
/// labels in order) selects the target member. When the payload carries no
/// configured label the strategy drops the message — label-routed units
/// deliberately do nothing on untagged input so humans stay in control of
/// what the unit picks up.
/// </para>
/// <para>
/// The target value in <paramref name="TriggerLabels"/> is the member's
/// address path — e.g. <c>backend-engineer</c> for an agent at
/// <c>agent://backend-engineer</c>, or <c>backend-team</c> for a sub-unit
/// at <c>unit://backend-team</c>. The strategy resolves the value against
/// the unit's current member list; a mapping that points outside the
/// membership is a no-op so a misconfigured label cannot exfiltrate work.
/// </para>
/// <para>
/// <paramref name="AddOnAssign"/> and <paramref name="RemoveOnAssign"/>
/// are the status-label round-trip hooks from #389: after a successful
/// assignment the connector is expected to apply the labels in
/// <paramref name="AddOnAssign"/> and strip the labels in
/// <paramref name="RemoveOnAssign"/>. The first ship of the strategy
/// records the intent in the orchestration-decision activity event only —
/// wiring it through the GitHub connector is tracked as follow-up work so
/// this PR stays scoped to the routing behaviour.
/// </para>
/// </remarks>
/// <param name="TriggerLabels">
/// Case-insensitive map from label name to the target member's address path.
/// A <c>null</c> or empty map means the unit has no label-routing rules and
/// the strategy will drop every message.
/// </param>
/// <param name="AddOnAssign">
/// Optional list of labels the connector should apply after a successful
/// assignment (e.g. <c>in-progress</c>). <c>null</c> = no additions.
/// </param>
/// <param name="RemoveOnAssign">
/// Optional list of labels the connector should strip after a successful
/// assignment — typically the trigger labels themselves so a second agent
/// does not race onto the same work. <c>null</c> = no removals.
/// </param>
public record LabelRoutingPolicy(
    IReadOnlyDictionary<string, string>? TriggerLabels = null,
    IReadOnlyList<string>? AddOnAssign = null,
    IReadOnlyList<string>? RemoveOnAssign = null);