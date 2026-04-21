// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Workflows.Activities;

using Cvoya.Spring.Core.Units;

/// <summary>
/// Output of the <c>PullImageActivity</c>. On success, <see cref="Success"/>
/// is <c>true</c> and <see cref="Failure"/> is <c>null</c>; on failure,
/// <see cref="Failure"/> carries a structured
/// <see cref="UnitValidationError"/> the workflow persists on the unit's
/// <c>LastValidationErrorJson</c> and transitions the unit to
/// <see cref="UnitStatus.Error"/>.
/// </summary>
/// <param name="Success"><c>true</c> when the image pulled and is ready to probe; <c>false</c> otherwise.</param>
/// <param name="Failure">
/// Structured failure payload — <c>null</c> on success.
/// <see cref="UnitValidationError.Step"/> is always
/// <see cref="UnitValidationStep.PullingImage"/> and <see cref="UnitValidationError.Code"/>
/// is typically <see cref="UnitValidationCodes.ImagePullFailed"/> or
/// <see cref="UnitValidationCodes.ImageStartFailed"/>.
/// </param>
public record PullImageActivityOutput(
    bool Success,
    UnitValidationError? Failure);