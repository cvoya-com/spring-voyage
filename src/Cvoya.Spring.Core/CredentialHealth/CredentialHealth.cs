// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.CredentialHealth;

/// <summary>
/// Projection of a <c>credential_health</c> row. Returned by
/// <see cref="ICredentialHealthStore"/> read methods.
/// </summary>
/// <param name="TenantId">Tenant that owns the credential-health row.</param>
/// <param name="Kind">Whether the subject is an agent runtime or connector.</param>
/// <param name="SubjectId">
/// Stable identifier of the subject — runtime <c>Id</c> for
/// <see cref="CredentialHealthKind.AgentRuntime"/>, connector slug for
/// <see cref="CredentialHealthKind.Connector"/>.
/// </param>
/// <param name="SecretName">
/// Opaque name identifying which of the subject's credentials this row
/// tracks. Convention for single-credential subjects is <c>"default"</c>;
/// multi-credential subjects (e.g. GitHub App id + private key) record one
/// row per logical credential.
/// </param>
/// <param name="Status">Current persistent state of the credential.</param>
/// <param name="LastError">
/// Human-readable explanation for a non-<see cref="CredentialHealthStatus.Valid"/>
/// status. <c>null</c> when the status is healthy or when the most recent
/// signal was a simple status-code transition without a richer payload.
/// </param>
/// <param name="LastChecked">Timestamp of the most recent status update.</param>
public sealed record CredentialHealth(
    Guid TenantId,
    CredentialHealthKind Kind,
    string SubjectId,
    string SecretName,
    CredentialHealthStatus Status,
    string? LastError,
    DateTimeOffset LastChecked);