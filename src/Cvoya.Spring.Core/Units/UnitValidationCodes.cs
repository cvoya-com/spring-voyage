// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Units;

/// <summary>
/// Stable string codes identifying known unit-validation failure modes. The
/// workflow emits one of these as the <see cref="UnitValidationError.Code"/>
/// value so UI, CLI, and log consumers can branch on a stable, machine-readable
/// identifier rather than the free-form <see cref="UnitValidationError.Message"/>.
/// Values are intentionally equal to the constant names.
/// </summary>
public static class UnitValidationCodes
{
    /// <summary>The container image could not be pulled from the registry.</summary>
    public const string ImagePullFailed = "ImagePullFailed";

    /// <summary>The image pulled but failed to start (bad entrypoint, immediate crash, etc.).</summary>
    public const string ImageStartFailed = "ImageStartFailed";

    /// <summary>The baseline tool the runtime requires was not found inside the running container.</summary>
    public const string ToolMissing = "ToolMissing";

    /// <summary>The declared credential was rejected on authentication by the remote service.</summary>
    public const string CredentialInvalid = "CredentialInvalid";

    /// <summary>The declared credential was rejected on format before it reached the remote service.</summary>
    public const string CredentialFormatRejected = "CredentialFormatRejected";

    /// <summary>The configured model identifier could not be resolved against the runtime's catalog or provider.</summary>
    public const string ModelNotFound = "ModelNotFound";

    /// <summary>The probe exceeded the configured timeout before returning a result.</summary>
    public const string ProbeTimeout = "ProbeTimeout";

    /// <summary>The probe failed with an unexpected internal error; details should be attached on <see cref="UnitValidationError.Details"/>.</summary>
    public const string ProbeInternalError = "ProbeInternalError";

    /// <summary>
    /// The actor failed to schedule the unit-validation workflow before any
    /// probe step ran. Host-side failure (Dapr workflow runtime unavailable,
    /// scheduler dependency unresolved, etc.) — not a probe failure. The
    /// unit is tombstoned into <see cref="UnitStatus.Error"/> so lifecycle
    /// operations (delete, revalidate) can proceed without operator
    /// knowledge of the API's <c>?force=true</c> escape hatch.
    /// </summary>
    public const string ScheduleFailed = "ScheduleFailed";

    /// <summary>
    /// The unit's persisted configuration is missing one or more values the
    /// validation workflow requires (e.g. no container image, no runtime id).
    /// Reported by the scheduler — the in-container probe never gets a chance
    /// to run. <see cref="UnitValidationError.Details"/> carries the missing
    /// field name(s) under the <c>missing</c> key. Distinct from
    /// <see cref="ScheduleFailed"/>, which is the catch-all for
    /// scheduler-side failures (Dapr runtime down, transient infra) — this
    /// code identifies a configuration mistake the operator can fix on the
    /// wizard's Execution step before retrying.
    /// </summary>
    public const string ConfigurationIncomplete = "ConfigurationIncomplete";
}