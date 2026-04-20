// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connectors;

using System.Text.Json;

/// <summary>
/// Tenant-scoped configuration for an installed
/// <see cref="IConnectorType"/>. The payload is opaque JSON — each
/// connector defines its own tenant-level config shape (default API base
/// URL, shared limits, organisation id) and reads it via
/// <see cref="Cvoya.Spring.Core.Tenancy.ITenantContext"/> +
/// <see cref="ITenantConnectorInstallService.GetAsync"/>.
/// </summary>
/// <param name="Config">
/// The opaque per-tenant config payload. <c>null</c> for connectors that
/// carry no tenant-level configuration (most OSS connectors today).
/// </param>
public sealed record ConnectorInstallConfig(JsonElement? Config)
{
    /// <summary>Empty config — no tenant-scoped payload.</summary>
    public static readonly ConnectorInstallConfig Empty = new((JsonElement?)null);
}