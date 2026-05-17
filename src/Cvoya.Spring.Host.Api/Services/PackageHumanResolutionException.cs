// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Api.Services;

using System;

/// <summary>
/// Thrown by <see cref="DefaultPackageArtefactActivator"/> when the
/// registered <see cref="Cvoya.Spring.Core.Packages.IPackageHumanResolutionPolicy"/>
/// returns <see cref="Cvoya.Spring.Core.Packages.PackageHumanResolutionOutcome.Rejected"/>
/// for a <c>humans[]</c> declaration. Surfaces through
/// <c>PackageInstallService</c>'s existing Phase-2 failure handling so
/// the operator sees the policy's reason rather than a generic
/// install failure.
/// </summary>
/// <remarks>
/// The hosted "reject" policy variant — "this tenant does not accept
/// package-declared humans" — is the primary intended cause. In OSS this
/// exception never fires (the OSS default returns
/// <c>Resolved</c> or <c>Skipped</c>; never <c>Rejected</c>).
/// </remarks>
public sealed class PackageHumanResolutionException : Exception
{
    /// <summary>
    /// Initialises a new <see cref="PackageHumanResolutionException"/>.
    /// </summary>
    /// <param name="role">
    /// The team role string from the manifest declaration that was
    /// rejected.
    /// </param>
    /// <param name="unitDisplayName">The display name of the target unit.</param>
    /// <param name="reason">
    /// The policy's reason text. Surfaced verbatim in the install-failure
    /// message so the operator gets actionable feedback.
    /// </param>
    public PackageHumanResolutionException(string role, string unitDisplayName, string? reason)
        : base(BuildMessage(role, unitDisplayName, reason))
    {
        Role = role;
        UnitDisplayName = unitDisplayName;
        Reason = reason;
    }

    /// <summary>The team role that was rejected.</summary>
    public string Role { get; }

    /// <summary>The display name of the target unit.</summary>
    public string UnitDisplayName { get; }

    /// <summary>The policy's reason text. May be null.</summary>
    public string? Reason { get; }

    private static string BuildMessage(string role, string unit, string? reason) =>
        string.IsNullOrWhiteSpace(reason)
            ? $"Package-declared human (role='{role}') on unit '{unit}' was rejected by the resolution policy."
            : $"Package-declared human (role='{role}') on unit '{unit}' was rejected by the resolution policy: {reason}";
}
