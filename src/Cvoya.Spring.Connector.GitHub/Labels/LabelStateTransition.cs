// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connector.GitHub.Labels;

using System.Text.Json.Serialization;

/// <summary>
/// Describes a single observed (or requested) transition between state labels.
/// Used both as the return of <see cref="LabelStateMachine.Derive"/> and as the
/// <c>state_transition</c> payload field carried by webhook-translated
/// <see cref="Cvoya.Spring.Core.Messaging.Message"/>s.
///
/// The <c>JsonPropertyName</c> attributes keep the serialized shape lowercase so
/// downstream consumers of translated webhook messages see a consistent
/// <c>from</c> / <c>to</c> / <c>trigger</c> / <c>legal</c> naming regardless of
/// the enclosing envelope's casing policy.
/// </summary>
/// <param name="From">The state the issue was in before the change. Null when bootstrapping from the configured <see cref="LabelStateMachineOptions.InitialState"/>.</param>
/// <param name="To">The new state the issue is in after the change. Null when the transition removes the only state label.</param>
/// <param name="Trigger">The action that produced this transition (e.g. <c>labeled</c>, <c>unlabeled</c>).</param>
/// <param name="Legal">Whether the derived transition is allowed by the configured state machine. Illegal transitions are still surfaced so downstream agents can react.</param>
public record LabelStateTransition(
    [property: JsonPropertyName("from")] string? From,
    [property: JsonPropertyName("to")] string? To,
    [property: JsonPropertyName("trigger")] string Trigger,
    [property: JsonPropertyName("legal")] bool Legal);