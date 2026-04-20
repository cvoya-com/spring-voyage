// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Data.Entities;

using System.Text.Json;

using Cvoya.Spring.Core.Tenancy;

/// <summary>
/// Row in <c>tenant_agent_runtime_installs</c> — records that a given tenant
/// has a given <see cref="Cvoya.Spring.Core.AgentRuntimes.IAgentRuntime"/>
/// installed, together with the tenant-specific configuration
/// (model catalog override, default model, optional base URL).
/// </summary>
public class TenantAgentRuntimeInstallEntity : ITenantScopedEntity
{
    /// <summary>Tenant that owns this install row.</summary>
    public string TenantId { get; set; } = string.Empty;

    /// <summary>
    /// Stable runtime identifier (e.g. <c>claude</c>, <c>openai</c>) matching
    /// <see cref="Cvoya.Spring.Core.AgentRuntimes.IAgentRuntime.Id"/>.
    /// </summary>
    public string RuntimeId { get; set; } = string.Empty;

    /// <summary>
    /// Tenant-scoped configuration for this runtime, stored as JSONB. Shape
    /// mirrors <see cref="Cvoya.Spring.Core.AgentRuntimes.AgentRuntimeInstallConfig"/>.
    /// </summary>
    public JsonElement? ConfigJson { get; set; }

    /// <summary>Timestamp when the runtime was first installed on the tenant.</summary>
    public DateTimeOffset InstalledAt { get; set; }

    /// <summary>Timestamp when the install row was last updated.</summary>
    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>Soft-delete marker — non-null rows are treated as uninstalled.</summary>
    public DateTimeOffset? DeletedAt { get; set; }
}