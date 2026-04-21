// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Workflows.Activities;

/// <summary>
/// Input to the <c>PullImageActivity</c> that the
/// <c>UnitValidationWorkflow</c> (T-04) invokes to pull a unit's container
/// image from its registry before probing it. The activity lives on the
/// dispatcher; this record carries only value-typed, JSON-serializable
/// fields so it can cross the workflow boundary.
/// </summary>
/// <param name="Image">The fully-qualified container image reference (e.g. <c>ghcr.io/cvoya/claude:1.2.3</c>).</param>
/// <param name="Timeout">Maximum wall-clock time the dispatcher will allow the pull to run before it reports <see cref="Cvoya.Spring.Core.Units.UnitValidationCodes.ImagePullFailed"/>.</param>
public record PullImageActivityInput(
    string Image,
    TimeSpan Timeout);