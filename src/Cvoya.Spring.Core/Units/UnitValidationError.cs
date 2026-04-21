// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Units;

using System.Collections.Generic;

/// <summary>
/// Structured, operator-facing outcome of a failed unit-validation probe run.
/// Persisted on the unit definition as <c>LastValidationErrorJson</c> on every
/// <see cref="UnitStatus.Validating"/> → <see cref="UnitStatus.Error"/> transition,
/// and surfaced to UI / CLI consumers so they can render the failed step, branch on
/// a stable <see cref="Code"/>, and read the redacted <see cref="Message"/>.
/// </summary>
/// <param name="Step">The probe step that was executing when the run failed.</param>
/// <param name="Code">A stable identifier from <see cref="UnitValidationCodes"/> — the value consumers should branch on.</param>
/// <param name="Message">A human-readable summary safe to display to operators. MUST be passed through the credential redactor before it is persisted here.</param>
/// <param name="Details">Optional structured key/value detail the probe chose to expose (e.g. HTTP status, registry endpoint, model id). Null when the probe had nothing to add beyond the code.</param>
public record UnitValidationError(
    UnitValidationStep Step,
    string Code,
    string Message,
    IReadOnlyDictionary<string, string>? Details);