// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Tenancy;

using Cvoya.Spring.Core.Security;

/// <summary>
/// First-class tenant record — the row that identifies a tenant on the
/// platform.
/// </summary>
/// <remarks>
/// <para>
/// The OSS data model has historically treated tenant identity as a
/// <em>value</em> stored on every <see cref="ITenantScopedEntity"/> row
/// rather than a row in a tenants table. <see cref="TenantRecord"/> is the
/// new registry surface that backs the
/// <c>/api/v1/platform/tenants</c> endpoints (C1.2d / #1260) — the CLI's
/// <c>spring tenant …</c> verbs, the portal's tenant management view, and
/// the cloud overlay's self-onboarding flow all operate on this shape.
/// </para>
/// <para>
/// <see cref="TenantRecord"/> is deliberately <em>not</em> an
/// <see cref="ITenantScopedEntity"/> — the registry is global by nature.
/// Cross-tenant reads of this table are legitimate and gated through the
/// <see cref="PlatformRoles.PlatformOperator"/> role at the API layer.
/// </para>
/// </remarks>
/// <param name="Id">
/// Stable identifier for the tenant (matches the value stored on
/// <see cref="ITenantScopedEntity.TenantId"/>). Lower-case, slug-shaped.
/// Never null or empty.
/// </param>
/// <param name="DisplayName">
/// Human-facing display name. Defaults to <paramref name="Id"/> when the
/// caller does not supply one on create.
/// </param>
/// <param name="State">Lifecycle state of this tenant.</param>
/// <param name="CreatedAt">When the tenant record was first persisted.</param>
/// <param name="UpdatedAt">When the tenant record was last modified.</param>
public sealed record TenantRecord(
    string Id,
    string DisplayName,
    TenantState State,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);