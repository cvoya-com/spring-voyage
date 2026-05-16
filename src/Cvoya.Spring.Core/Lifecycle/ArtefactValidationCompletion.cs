// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Lifecycle;

using System.Runtime.Serialization;

using Cvoya.Spring.Core.Lifecycle;

/// <summary>
/// Terminal callback payload the <c>ArtefactValidationWorkflow</c> posts back to
/// <c>IUnitActor.CompleteValidationAsync</c> at the end of a validation run.
/// The actor uses it to drive the <see cref="LifecycleStatus.Validating"/> →
/// <see cref="LifecycleStatus.Stopped"/> or <see cref="LifecycleStatus.Validating"/> →
/// <see cref="LifecycleStatus.Error"/> transition and to persist the redacted
/// <see cref="Failure"/> blob on failure (null on success).
/// </summary>
/// <remarks>
/// Travels across the Dapr Actor remoting boundary as the argument to
/// <c>IUnitActor.CompleteValidationAsync</c>. Dapr remoting uses
/// <c>DataContractSerializer</c>, which can serialize a positional record
/// only when every property is explicitly opted in with
/// <c>[DataContract]</c> + <c>[DataMember(Order = N)]</c> — otherwise it
/// requires a parameterless constructor that positional records don't
/// synthesize. Matches the convention established on
/// <see cref="TransitionResult"/> and <see cref="UnitMetadata"/>.
/// </remarks>
/// <param name="Success">True when every probe step succeeded, false when any step failed.</param>
/// <param name="Failure">Structured failure payload — non-null iff <see cref="Success"/> is false.</param>
/// <param name="WorkflowInstanceId">Workflow instance id the workflow was scheduled under. The actor uses it as a stale-run guard: a completion whose id does not match the unit's current <c>LastValidationRunId</c> is a no-op.</param>
[DataContract]
public sealed record ArtefactValidationCompletion(
    [property: DataMember(Order = 0)] bool Success,
    [property: DataMember(Order = 1)] ArtefactValidationError? Failure,
    [property: DataMember(Order = 2)] string WorkflowInstanceId);
