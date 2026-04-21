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
}