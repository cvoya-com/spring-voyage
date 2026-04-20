// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.AgentRuntimes;

/// <summary>
/// Projection of a <c>tenant_agent_runtime_installs</c> row. Returned by
/// <see cref="ITenantAgentRuntimeInstallService"/> read methods.
/// </summary>
/// <param name="RuntimeId">Stable runtime id (matches <see cref="IAgentRuntime.Id"/>).</param>
/// <param name="TenantId">Tenant that owns the install row.</param>
/// <param name="Config">Tenant-scoped configuration for this runtime.</param>
/// <param name="InstalledAt">Timestamp when the runtime was first installed on the tenant.</param>
/// <param name="UpdatedAt">Timestamp when the install row was last updated.</param>
public sealed record InstalledAgentRuntime(
    string RuntimeId,
    string TenantId,
    AgentRuntimeInstallConfig Config,
    DateTimeOffset InstalledAt,
    DateTimeOffset UpdatedAt);