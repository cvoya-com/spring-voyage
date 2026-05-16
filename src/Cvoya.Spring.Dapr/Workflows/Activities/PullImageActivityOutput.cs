// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Workflows.Activities;

using Cvoya.Spring.Core.Lifecycle;
using Cvoya.Spring.Core.Units;

/// <summary>
/// Output of the <c>PullImageActivity</c>. On success, <see cref="Success"/>
/// is <c>true</c> and <see cref="Failure"/> is <c>null</c>; on failure,
/// <see cref="Failure"/> carries a structured
/// <see cref="ArtefactValidationError"/> the workflow persists on the unit's
/// <c>LastValidationErrorJson</c> and transitions the unit to
/// <see cref="LifecycleStatus.Error"/>.
/// </summary>
/// <param name="Success"><c>true</c> when the image pulled and is ready to probe; <c>false</c> otherwise.</param>
/// <param name="Failure">
/// Structured failure payload — <c>null</c> on success.
/// <see cref="ArtefactValidationError.Step"/> is always
/// <see cref="ArtefactValidationStep.PullingImage"/> and <see cref="ArtefactValidationError.Code"/>
/// is typically <see cref="ArtefactValidationCodes.ImagePullFailed"/> or
/// <see cref="ArtefactValidationCodes.ImageStartFailed"/>.
/// </param>
public record PullImageActivityOutput(
    bool Success,
    ArtefactValidationError? Failure);
