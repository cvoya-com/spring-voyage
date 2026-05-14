// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.ModelProviders;

/// <summary>
/// Projection of a <c>tenant_model_provider_installs</c> row. Returned by
/// <see cref="ITenantModelProviderInstallService"/> read methods.
/// </summary>
/// <param name="ProviderId">Stable provider id (matches an entry in <c>eng/runtime-catalog/runtime-catalog.yaml</c> § <c>modelProviders</c>).</param>
/// <param name="TenantId">Tenant that owns the install row.</param>
/// <param name="Config">Tenant-scoped configuration for this provider.</param>
/// <param name="InstalledAt">Timestamp when the provider was first installed on the tenant.</param>
/// <param name="UpdatedAt">Timestamp when the install row was last updated.</param>
public sealed record InstalledModelProvider(
    string ProviderId,
    Guid TenantId,
    ModelProviderInstallConfig Config,
    DateTimeOffset InstalledAt,
    DateTimeOffset UpdatedAt);
