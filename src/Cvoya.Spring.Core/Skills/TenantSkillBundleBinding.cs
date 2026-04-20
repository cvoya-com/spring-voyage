// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Skills;

/// <summary>
/// Projection of a <c>tenant_skill_bundle_bindings</c> row — records
/// that a tenant has opted into (or out of) a given skill bundle.
/// </summary>
/// <param name="TenantId">Tenant that owns the binding row.</param>
/// <param name="BundleId">
/// Stable bundle identifier (the package directory name under
/// <c>Skills:PackagesRoot</c>, e.g. <c>software-engineering</c>).
/// </param>
/// <param name="Enabled">
/// <c>true</c> when the tenant can resolve and bind units against this
/// bundle. Disabled rows are kept for audit purposes — a later rebind
/// flips them back to <c>true</c> without losing the original
/// <see cref="BoundAt"/> timestamp.
/// </param>
/// <param name="BoundAt">Timestamp when the binding was first created.</param>
public sealed record TenantSkillBundleBinding(
    string TenantId,
    string BundleId,
    bool Enabled,
    DateTimeOffset BoundAt);