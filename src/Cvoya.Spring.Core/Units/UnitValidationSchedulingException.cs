// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Units;

/// <summary>
/// Thrown by an <see cref="IUnitValidationWorkflowScheduler"/> implementation
/// when it can determine — without running the in-container probes — that the
/// unit's configuration is incomplete or the workflow infrastructure cannot be
/// reached. Carries a fully-formed <see cref="UnitValidationError"/> so the
/// caller (typically <c>UnitActor.TryStartValidationWorkflowAsync</c>) can
/// persist it on the unit's <c>LastValidationErrorJson</c> column and flip the
/// unit straight into <see cref="UnitStatus.Error"/> instead of leaving it
/// stuck in <see cref="UnitStatus.Validating"/> with no workflow running.
/// </summary>
/// <remarks>
/// <para>
/// Use this exception only for failures the operator can act on (configuration
/// gaps the wizard can name a missing field for — see
/// <see cref="UnitValidationCodes.ConfigurationIncomplete"/>). Transient
/// infrastructure errors (e.g. a momentary Dapr workflow gateway hiccup) should
/// surface as a regular <see cref="System.Exception"/>; the actor still recovers
/// by transitioning to <see cref="UnitStatus.Error"/> with the catch-all
/// <see cref="UnitValidationCodes.ScheduleFailed"/> code so an operator can
/// retry via <c>/revalidate</c>.
/// </para>
/// </remarks>
public class UnitValidationSchedulingException : SpringException
{
    /// <summary>
    /// Constructs a new scheduling exception that carries the structured error
    /// the actor should persist on the unit row.
    /// </summary>
    /// <param name="error">The validation error to persist.</param>
    public UnitValidationSchedulingException(UnitValidationError error)
        : base(error?.Message ?? "Unit validation could not be scheduled.")
    {
        ArgumentNullException.ThrowIfNull(error);
        Error = error;
    }

    /// <summary>
    /// The structured failure to persist on the unit's
    /// <c>LastValidationErrorJson</c> column. Never null.
    /// </summary>
    public UnitValidationError Error { get; }
}