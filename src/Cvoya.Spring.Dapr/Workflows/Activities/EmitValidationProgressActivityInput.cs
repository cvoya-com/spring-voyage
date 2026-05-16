// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Workflows.Activities;

using Cvoya.Spring.Core.Artefacts;
using Cvoya.Spring.Core.Lifecycle;

/// <summary>
/// Input to <c>EmitValidationProgressActivity</c>. The
/// <see cref="ArtefactValidationWorkflow"/> can't directly inject
/// <see cref="Cvoya.Spring.Core.Capabilities.IActivityEventBus"/> (Dapr
/// workflow bodies must stay deterministic + service-free), so it emits
/// every progress event via this tiny activity.
/// </summary>
/// <param name="Kind">Whether the artefact under validation is a Unit or an Agent — chooses the emitted event's address scheme (<c>unit</c> vs <c>agent</c>) so the portal's SSE filter routes it to the right detail page.</param>
/// <param name="ArtefactId">The artefact's stable Guid identity; travels as the <see cref="Cvoya.Spring.Core.Messaging.Address.Id"/> on the emitted event.</param>
/// <param name="Step">The probe step this event is reporting on.</param>
/// <param name="Status">Transition of the step — typically <c>Running</c>, <c>Succeeded</c>, or <c>Failed</c>. Strings (not an enum) so the set can grow without re-deploying the web filter, matching the T-06 front-end note.</param>
/// <param name="Code">Stable <see cref="ArtefactValidationCodes"/> code — populated only when <paramref name="Status"/> is <c>Failed</c>; <c>null</c> on <c>Running</c> / <c>Succeeded</c>.</param>
public record EmitValidationProgressActivityInput(
    ArtefactKind Kind,
    Guid ArtefactId,
    ArtefactValidationStep Step,
    string Status,
    string? Code);
