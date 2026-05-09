// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Data.Entities;

using System.Text.Json;

using Cvoya.Spring.Core.Tenancy;

/// <summary>
/// Row in <c>tenant_model_provider_installs</c> — records that a given
/// tenant has a given <see cref="Cvoya.Spring.Core.Catalog.ModelProvider"/>
/// installed, together with the tenant-specific configuration (model
/// catalogue override, default model, optional base URL). Per ADR-0038
/// installs are keyed on <c>(tenant, provider)</c>; runtime selection
/// is per-unit binding, not a per-tenant install row.
/// </summary>
public class TenantModelProviderInstallEntity : ITenantScopedEntity
{
    /// <summary>Tenant that owns this install row.</summary>
    public Guid TenantId { get; set; }

    /// <summary>
    /// Stable provider identifier (e.g. <c>anthropic</c>, <c>openai</c>) —
    /// matches an entry in <c>platform/runtime-catalog.yaml</c>'s
    /// <c>modelProviders</c> list.
    /// </summary>
    public string ProviderId { get; set; } = string.Empty;

    /// <summary>
    /// Tenant-scoped configuration for this provider, stored as JSONB.
    /// Shape mirrors <see cref="Cvoya.Spring.Core.ModelProviders.ModelProviderInstallConfig"/>
    /// (the same triple <c>{ models, defaultModel, baseUrl }</c>; field
    /// names are preserved for v0.1 to keep persistence stable while the
    /// Web API DTOs reshape in PR-1b).
    /// </summary>
    public JsonElement? ConfigJson { get; set; }

    /// <summary>Timestamp when the provider was first installed on the tenant.</summary>
    public DateTimeOffset InstalledAt { get; set; }

    /// <summary>Timestamp when the install row was last updated.</summary>
    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>Soft-delete marker — non-null rows are treated as uninstalled.</summary>
    public DateTimeOffset? DeletedAt { get; set; }
}
