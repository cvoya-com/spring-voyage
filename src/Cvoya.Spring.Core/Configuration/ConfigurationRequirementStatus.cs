// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Configuration;

/// <summary>
/// Immutable outcome of evaluating a single <see cref="IConfigurationRequirement"/>.
/// Produced by <see cref="IConfigurationRequirement.ValidateAsync"/> and aggregated
/// by the startup validator into the top-level <see cref="ConfigurationReport"/>.
/// </summary>
/// <remarks>
/// <para>
/// Three fields carry the user-facing narration:
/// </para>
/// <list type="bullet">
///   <item><c>Reason</c> — what the validator found (used in both the happy path and the unhappy path).</item>
///   <item><c>Suggestion</c> — what to do about it (usually set only when <see cref="Status"/> is not <see cref="ConfigurationStatus.Met"/>).</item>
///   <item><c>FatalError</c> — the exception to surface if the requirement is <see cref="IConfigurationRequirement.IsMandatory"/> and <see cref="Status"/> is <see cref="ConfigurationStatus.Invalid"/>. The validator throws this from its <c>StartAsync</c> to abort host startup.</item>
/// </list>
/// <para>
/// This type is part of the extension contract — the private cloud host and
/// third-party consumers construct instances directly. It is therefore a
/// public record with init-only setters, not <c>sealed</c>.
/// </para>
/// </remarks>
public sealed record ConfigurationRequirementStatus
{
    /// <summary>
    /// Terminal state of the requirement. See <see cref="ConfigurationStatus"/>.
    /// </summary>
    public required ConfigurationStatus Status { get; init; }

    /// <summary>
    /// Advisory severity that accompanies the status. Drives badge colour and
    /// tone in the portal and CLI; does not affect the abort-on-boot rule.
    /// Defaults to <see cref="SeverityLevel.Information"/>.
    /// </summary>
    public SeverityLevel Severity { get; init; } = SeverityLevel.Information;

    /// <summary>
    /// Short human-readable explanation of the outcome. Required for every
    /// non-<see cref="ConfigurationStatus.Met"/> result; recommended for
    /// "met but degraded" (<see cref="SeverityLevel.Warning"/>) results too.
    /// </summary>
    public string? Reason { get; init; }

    /// <summary>
    /// Actionable next step. Usually set only when <see cref="Status"/> is
    /// <see cref="ConfigurationStatus.Disabled"/> or
    /// <see cref="ConfigurationStatus.Invalid"/>. Rendered verbatim — so
    /// include the env-var name, the CLI helper, and any docs link the
    /// operator needs.
    /// </summary>
    public string? Suggestion { get; init; }

    /// <summary>
    /// Exception to throw from the startup validator when
    /// <see cref="IConfigurationRequirement.IsMandatory"/> is <c>true</c> and
    /// <see cref="Status"/> is <see cref="ConfigurationStatus.Invalid"/>.
    /// Ignored in every other combination.
    /// </summary>
    public Exception? FatalError { get; init; }

    /// <summary>
    /// Helper for the happy path — the requirement is satisfied and the
    /// validator has nothing to say beyond "yes".
    /// </summary>
    public static ConfigurationRequirementStatus Met() =>
        new() { Status = ConfigurationStatus.Met };

    /// <summary>
    /// Helper for the happy-but-degraded path — the requirement is
    /// satisfied but the validator wants operators to know about a caveat
    /// (ephemeral dev key, default endpoint, etc.).
    /// </summary>
    /// <param name="reason">Short description of the degradation.</param>
    /// <param name="suggestion">Optional recommendation; may be <c>null</c>.</param>
    public static ConfigurationRequirementStatus MetWithWarning(
        string reason, string? suggestion = null) =>
        new()
        {
            Status = ConfigurationStatus.Met,
            Severity = SeverityLevel.Warning,
            Reason = reason,
            Suggestion = suggestion,
        };

    /// <summary>
    /// Helper for optional features that aren't configured. Always pairs with
    /// <see cref="IConfigurationRequirement.IsMandatory"/> <c>false</c>; if the
    /// requirement is mandatory the validator treats this the same as
    /// <see cref="ConfigurationStatus.Invalid"/>.
    /// </summary>
    /// <param name="reason">Short description of what's missing.</param>
    /// <param name="suggestion">Actionable next step; rendered verbatim.</param>
    public static ConfigurationRequirementStatus Disabled(
        string reason, string? suggestion = null) =>
        new()
        {
            Status = ConfigurationStatus.Disabled,
            Severity = SeverityLevel.Warning,
            Reason = reason,
            Suggestion = suggestion,
        };

    /// <summary>
    /// Helper for misconfiguration — the requirement is set but the value
    /// can't be used. When the owning requirement is mandatory, the
    /// validator throws <paramref name="fatalError"/> from
    /// <c>StartAsync</c> and aborts host boot.
    /// </summary>
    /// <param name="reason">Short description of what's wrong.</param>
    /// <param name="suggestion">Actionable next step.</param>
    /// <param name="fatalError">
    /// Exception to throw on mandatory-invalid. If <c>null</c>, the
    /// validator constructs a generic <see cref="InvalidOperationException"/>
    /// from <paramref name="reason"/> when it needs to abort.
    /// </param>
    public static ConfigurationRequirementStatus Invalid(
        string reason,
        string? suggestion = null,
        Exception? fatalError = null) =>
        new()
        {
            Status = ConfigurationStatus.Invalid,
            Severity = SeverityLevel.Error,
            Reason = reason,
            Suggestion = suggestion,
            FatalError = fatalError,
        };
}