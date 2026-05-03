// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Data.Entities;

using Cvoya.Spring.Core.CredentialHealth;
using Cvoya.Spring.Core.Tenancy;

/// <summary>
/// Row in <c>credential_health</c> — the persistent state machine for a
/// single credential attached to an agent runtime or connector. Shared
/// schema so wizard banners and admin read-outs can enumerate runtime
/// and connector health via a single read.
///
/// <para>
/// <see cref="Kind"/> discriminates the subject scope. <see cref="SubjectId"/>
/// is the catalog slug for both kinds — agent runtime ids
/// (<c>claude</c>, <c>openai</c>, …) and connector kind slugs
/// (<c>github</c>, …) are content-addressable identifiers owned by the
/// package author and stable across tenants; they intentionally remain
/// strings.
/// </para>
/// </summary>
public class CredentialHealthEntity : ITenantScopedEntity
{
    /// <summary>Tenant that owns this row.</summary>
    public Guid TenantId { get; set; }

    /// <summary>Whether the subject is an agent runtime or a connector.</summary>
    public CredentialHealthKind Kind { get; set; }

    /// <summary>
    /// Catalog slug identifying the subject — runtime <c>Id</c> for
    /// <see cref="CredentialHealthKind.AgentRuntime"/>, connector type
    /// slug for <see cref="CredentialHealthKind.Connector"/>.
    /// </summary>
    public string SubjectId { get; set; } = string.Empty;

    /// <summary>
    /// Secret name within the subject. Convention for single-credential
    /// subjects is <c>"default"</c>.
    /// </summary>
    public string SecretName { get; set; } = string.Empty;

    /// <summary>Current persistent status.</summary>
    public CredentialHealthStatus Status { get; set; }

    /// <summary>Human-readable explanation for non-healthy statuses, or <c>null</c>.</summary>
    public string? LastError { get; set; }

    /// <summary>Timestamp of the most recent status update.</summary>
    public DateTimeOffset LastChecked { get; set; }
}
