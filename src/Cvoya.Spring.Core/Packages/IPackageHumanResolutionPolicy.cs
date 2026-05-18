// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Packages;

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

/// <summary>
/// Resolves a single package-declared <c>humans:</c> entry to zero or more
/// concrete human <see cref="Guid"/>s at install time. The single DI seam
/// the cloud overlay swaps to control identity binding for package-declared
/// team members (ADR-0044 § 4); the OSS default in <c>Cvoya.Spring.Dapr</c>
/// auto-fills every declared role with the install caller's UUID.
/// </summary>
/// <remarks>
/// <para>
/// <b>Design context (ADR-0044).</b> Packages declare team membership using
/// <c>{ role, expertise, notifications }</c>; the package author cannot
/// know who will install the package or which tenant members exist at
/// install time. The activator therefore calls into this policy once per
/// declared <c>humans[]</c> entry; the policy returns the set of concrete
/// human Guids that should land in the new
/// <c>unit_memberships_humans</c> table for that declaration.
/// </para>
/// <para>
/// <b>Set semantics (ADR-0044 § 3).</b> The activator upserts one row per
/// returned Guid into a table whose unique index is
/// <c>(tenant_id, unit_id, human_id, role)</c>. Multiple declarations that
/// resolve to the same <c>(human, role)</c> pair collapse to a single row;
/// distinct humans for the same role land as distinct rows. The policy
/// returning multiple Guids for one declaration is legitimate — a hosted
/// policy may map a single <c>humans: [{role: reviewer}]</c> declaration
/// to several tenant members when the tenant rule says "all admins fill
/// the reviewer slot".
/// </para>
/// <para>
/// <b>DI registration.</b> <c>Cvoya.Spring.Dapr</c> registers the OSS
/// default via <c>TryAddSingleton</c> so the cloud overlay can pre-register
/// its hosted variant; the cloud registration wins.
/// </para>
/// </remarks>
public interface IPackageHumanResolutionPolicy
{
    /// <summary>
    /// Resolves a single <c>humans[]</c> declaration to a
    /// <see cref="PackageHumanResolution"/>. Implementations:
    /// <list type="bullet">
    /// <item><description>
    /// Return <see cref="PackageHumanResolutionOutcome.Resolved"/> with at
    /// least one Guid when the declaration binds to one or more concrete
    /// humans. The activator persists one membership row per Guid,
    /// idempotent on the unique index.
    /// </description></item>
    /// <item><description>
    /// Return <see cref="PackageHumanResolutionOutcome.Skipped"/> with an
    /// empty Guid list when the declaration intentionally produces no row
    /// (e.g. out-of-request install with no caller identity; hosted
    /// "prompt-per-slot" policy deferring to a follow-up task).
    /// </description></item>
    /// <item><description>
    /// Return <see cref="PackageHumanResolutionOutcome.Rejected"/> with an
    /// empty Guid list when the declaration must fail the install (e.g.
    /// hosted "reject" policy that refuses package-declared humans). The
    /// activator surfaces this as a <c>PackageHumanResolutionException</c>
    /// and the install fails.
    /// </description></item>
    /// </list>
    /// </summary>
    Task<PackageHumanResolution> ResolveAsync(
        PackageHumanResolutionRequest request,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Input to <see cref="IPackageHumanResolutionPolicy.ResolveAsync"/>: a
/// single <c>humans[]</c> declaration combined with the install-time
/// context the policy needs to bind it to concrete humans.
/// </summary>
/// <param name="TenantId">
/// The tenant the install is targeting. Implementations consulting
/// per-tenant rules read tenant-scoped state through this id (rather than
/// re-resolving from an ambient <c>ITenantContext</c> — the policy is
/// expected to be a singleton).
/// </param>
/// <param name="UnitId">
/// The unit the membership row will be written against. Already minted by
/// Phase 1 of the package install pipeline so the policy can correlate
/// per-unit decisions (e.g. "this hosted tenant's policy is per-unit").
/// </param>
/// <param name="UnitDisplayName">
/// Human-friendly label for the unit, surfaced verbatim from the manifest.
/// Provided for log lines and for hosted policies that may want to render
/// a "who fills role X on unit Y?" prompt.
/// </param>
/// <param name="Roles">
/// Free-form team-role strings from the manifest (ADR-0045 §3). Multi-
/// valued; may be empty when the package author declared a participant
/// without explicit roles.
/// </param>
/// <param name="Expertise">
/// The manifest's <c>expertise:</c> tags for this declaration. Empty list
/// when the manifest omitted the field.
/// </param>
/// <param name="Notifications">
/// The manifest's <c>notifications:</c> tags for this declaration. Empty
/// list when the manifest omitted the field.
/// </param>
/// <param name="DisplayName">
/// Optional human-friendly display name from the manifest's
/// <c>displayName:</c> field on a <c>- human:</c> entry. <see langword="null"/>
/// when omitted; the resolution policy is free to derive a default
/// (e.g. <c>"Operator · &lt;roles[0]&gt;"</c> for the OSS policy).
/// </param>
/// <param name="Description">
/// Optional description from the manifest's <c>description:</c> field on a
/// <c>- human:</c> entry. <see langword="null"/> when omitted.
/// </param>
/// <param name="InstallCallerHumanId">
/// The install caller's stable UUID (resolved via the API host's
/// <c>IAuthenticatedCallerAccessor</c>). <see langword="null"/> when the
/// install path runs out-of-request (worker host, background reinstall).
/// The OSS default no longer auto-fills with the caller (ADR-0045 §10) —
/// it mints a fresh <c>HumanEntity</c> per declaration — but the field
/// remains on the request for hosted policies that bind by claim.
/// </param>
public sealed record PackageHumanResolutionRequest(
    Guid TenantId,
    Guid UnitId,
    string UnitDisplayName,
    IReadOnlyList<string> Roles,
    IReadOnlyList<string> Expertise,
    IReadOnlyList<string> Notifications,
    string? DisplayName,
    string? Description,
    Guid? InstallCallerHumanId);

/// <summary>
/// Output of <see cref="IPackageHumanResolutionPolicy.ResolveAsync"/>. The
/// <see cref="Outcome"/> discriminates the three legitimate cases; the
/// <see cref="HumanIds"/> list carries zero or more Guids per the
/// outcome's contract.
/// </summary>
/// <param name="Outcome">
/// The resolution outcome. Determines the activator's downstream behaviour
/// (write rows / log and continue / fail the install).
/// </param>
/// <param name="HumanIds">
/// The resolved human Guids when <see cref="Outcome"/> is
/// <see cref="PackageHumanResolutionOutcome.Resolved"/>; otherwise an
/// empty list. Implementations returning multiple Guids per call have the
/// activator write one membership row per Guid, with the row collapse
/// semantics in ADR-0044 § 3 applied via the unique index on
/// <c>(tenant_id, unit_id, human_id, role)</c>.
/// </param>
/// <param name="Reason">
/// Optional human-readable reason. Surfaced in logs on
/// <see cref="PackageHumanResolutionOutcome.Skipped"/> and in the
/// install-failure message on
/// <see cref="PackageHumanResolutionOutcome.Rejected"/>.
/// </param>
public sealed record PackageHumanResolution(
    PackageHumanResolutionOutcome Outcome,
    IReadOnlyList<Guid> HumanIds,
    string? Reason = null);

/// <summary>
/// Outcome of a single <see cref="IPackageHumanResolutionPolicy.ResolveAsync"/>
/// call. See <see cref="IPackageHumanResolutionPolicy"/> for the activator's
/// handling of each value.
/// </summary>
public enum PackageHumanResolutionOutcome
{
    /// <summary>
    /// The policy resolved the declaration to one or more concrete human
    /// Guids. The activator upserts one membership row per Guid.
    /// </summary>
    Resolved,

    /// <summary>
    /// The policy intentionally skipped the declaration. The activator
    /// logs at Information and proceeds with the next declaration without
    /// writing any row.
    /// </summary>
    Skipped,

    /// <summary>
    /// The policy refused the declaration. The activator throws a
    /// <c>PackageHumanResolutionException</c> carrying the
    /// <see cref="PackageHumanResolution.Reason"/>; the surrounding
    /// install pipeline surfaces it as a Phase-2 failure.
    /// </summary>
    Rejected,
}
