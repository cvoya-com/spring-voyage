// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.AgentRuntimes;

using System.Collections.Generic;

using Cvoya.Spring.Core.Units;

/// <summary>
/// Outcome produced by <see cref="ProbeStep.InterpretOutput"/> after a
/// container-side probe step runs. Carries either a successful verdict
/// (with optional extras the workflow may forward to the next step) or a
/// structured failure with a stable <see cref="Code"/> from
/// <see cref="UnitValidationCodes"/>.
/// </summary>
/// <remarks>
/// <para>
/// Uses a discriminator-plus-factory shape (matching
/// <see cref="FetchLiveModelsResult"/> in the same folder) rather than an
/// abstract-record hierarchy. The <see cref="Outcome"/> flag tells the
/// caller which branch the result belongs to; callers MUST inspect it
/// before reading <see cref="Code"/>, <see cref="Message"/>, or
/// <see cref="Details"/>.
/// </para>
/// <para>
/// <b>Extras channel.</b> The <see cref="Extras"/> dictionary lets a
/// successful step pass structured data to a later step without a second
/// trip to the container. The canonical example is the
/// <see cref="UnitValidationStep.ResolvingModel"/> step: when the probe
/// succeeds, <see cref="Extras"/> carries the live model list (for
/// example, <c>{"models": "claude-sonnet-4,claude-haiku-4"}</c>) so
/// downstream code can render or persist the catalog without reissuing
/// the request.
/// </para>
/// </remarks>
/// <param name="Outcome">
/// Whether this step succeeded or failed. Callers MUST branch on this
/// before reading <see cref="Code"/> / <see cref="Message"/>.
/// </param>
/// <param name="Code">
/// A stable identifier from <see cref="UnitValidationCodes"/> when
/// <see cref="Outcome"/> is <see cref="StepOutcome.Failed"/>; <c>null</c>
/// on success.
/// </param>
/// <param name="Message">
/// Human-readable summary safe to surface to operators on failure;
/// <c>null</c> on success. Caller (the workflow) is responsible for
/// passing this through <see cref="Security.CredentialRedactor"/> before
/// persisting it, but the standard probe path already redacts stdout /
/// stderr before the interpreter sees it, so implementers typically can
/// interpolate trimmed container output into this field safely.
/// </param>
/// <param name="Details">
/// Optional structured key/value detail the probe chose to expose (for
/// example, <c>{"http_status": "401"}</c>). <c>null</c> when the probe
/// had nothing to add beyond <see cref="Code"/>.
/// </param>
/// <param name="Extras">
/// Optional structured success payload the probe chose to expose (for
/// example, <c>{"models": "gpt-4o,gpt-4o-mini"}</c>). <c>null</c> when
/// the probe has nothing to pass forward.
/// </param>
public sealed record StepResult(
    StepOutcome Outcome,
    string? Code,
    string? Message,
    IReadOnlyDictionary<string, string>? Details,
    IReadOnlyDictionary<string, string>? Extras)
{
    /// <summary>
    /// Factory for a successful step result. Pass <paramref name="extras"/>
    /// to forward structured data to a later step (the canonical consumer
    /// is <see cref="UnitValidationStep.ResolvingModel"/>, which emits the
    /// live model list under the key <c>"models"</c>).
    /// </summary>
    /// <param name="extras">Optional structured payload to forward; <c>null</c> when the step has nothing to emit.</param>
    public static StepResult Succeed(IReadOnlyDictionary<string, string>? extras = null) =>
        new(StepOutcome.Succeeded, Code: null, Message: null, Details: null, Extras: extras);

    /// <summary>
    /// Factory for a failed step result. <paramref name="code"/> should be
    /// one of the constants on <see cref="UnitValidationCodes"/> so UI /
    /// CLI consumers can branch on it stably.
    /// </summary>
    /// <param name="code">Stable failure identifier from <see cref="UnitValidationCodes"/>.</param>
    /// <param name="message">Operator-facing summary. MUST NOT contain the raw credential value.</param>
    /// <param name="details">Optional structured detail (for example, <c>{"http_status": "401"}</c>).</param>
    public static StepResult Fail(
        string code,
        string message,
        IReadOnlyDictionary<string, string>? details = null) =>
        new(StepOutcome.Failed, Code: code, Message: message, Details: details, Extras: null);
}

/// <summary>
/// Discriminator for <see cref="StepResult"/> — whether the step
/// succeeded or failed.
/// </summary>
public enum StepOutcome
{
    /// <summary>The probe step completed successfully.</summary>
    Succeeded,

    /// <summary>The probe step failed; see <see cref="StepResult.Code"/> + <see cref="StepResult.Message"/>.</summary>
    Failed,
}