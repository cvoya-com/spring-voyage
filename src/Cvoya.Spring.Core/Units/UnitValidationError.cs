// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Units;

using System.Collections.Generic;
using System.Runtime.Serialization;

using Cvoya.Spring.Core.Lifecycle;

/// <summary>
/// Structured, operator-facing outcome of a failed unit-validation probe run.
/// Persisted on the unit definition as <c>LastValidationErrorJson</c> on every
/// <see cref="LifecycleStatus.Validating"/> → <see cref="LifecycleStatus.Error"/> transition,
/// and surfaced to UI / CLI consumers so they can render the failed step, branch on
/// a stable <see cref="Code"/>, and read the redacted <see cref="Message"/>.
/// </summary>
/// <remarks>
/// Travels across the Dapr Actor remoting boundary as a property on
/// <see cref="UnitValidationCompletion"/>. Dapr remoting uses
/// <c>DataContractSerializer</c>, which can only serialize positional records
/// when every property is explicitly opted in with <c>[DataContract]</c> +
/// <c>[DataMember(Order = N)]</c>. Matches the convention established on
/// <see cref="TransitionResult"/> and <see cref="UnitMetadata"/>.
/// </remarks>
/// <param name="Step">The probe step that was executing when the run failed.</param>
/// <param name="Code">A stable identifier from <see cref="UnitValidationCodes"/> — the value consumers should branch on.</param>
/// <param name="Message">A human-readable summary safe to display to operators. MUST be passed through the credential redactor before it is persisted here.</param>
/// <param name="Details">Optional structured key/value detail the probe chose to expose (e.g. HTTP status, registry endpoint, model id). Null when the probe had nothing to add beyond the code.</param>
[DataContract]
public record UnitValidationError(
    [property: DataMember(Order = 0)] UnitValidationStep Step,
    [property: DataMember(Order = 1)] string Code,
    [property: DataMember(Order = 2)] string Message,
    [property: DataMember(Order = 3)] IReadOnlyDictionary<string, string>? Details);
