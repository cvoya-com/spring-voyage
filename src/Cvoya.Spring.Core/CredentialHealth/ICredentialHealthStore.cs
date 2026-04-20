// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.CredentialHealth;

/// <summary>
/// Persistent store for credential-health signals shared across agent
/// runtimes and connectors. The store is tenant-scoped — the ambient
/// <see cref="Tenancy.ITenantContext"/> resolves the owning tenant on
/// every read and write, so callers never pass a <c>tenantId</c>.
/// </summary>
/// <remarks>
/// <para>
/// Two writers feed this store: accept-time validation (the install /
/// validate-credential endpoints call the subject's
/// <c>ValidateCredentialAsync</c> and record the outcome) and use-time
/// watchdog middleware (the HTTP watchdog handler inspects
/// 401/403/similar responses from the subject's outbound traffic and
/// flips the status). Readers are the wizard banner, portal read-only
/// views, and the <c>spring … credentials status</c> CLI verb.
/// </para>
/// </remarks>
public interface ICredentialHealthStore
{
    /// <summary>
    /// Upserts the row for <c>(tenant, kind, subjectId, secretName)</c>.
    /// Sets <see cref="CredentialHealth.Status"/> to <paramref name="status"/>,
    /// <see cref="CredentialHealth.LastError"/> to <paramref name="lastError"/>,
    /// and <see cref="CredentialHealth.LastChecked"/> to <c>UtcNow</c>.
    /// </summary>
    /// <param name="kind">Whether the subject is a runtime or connector.</param>
    /// <param name="subjectId">Stable runtime id or connector slug.</param>
    /// <param name="secretName">Secret key within the subject (convention: <c>"default"</c>).</param>
    /// <param name="status">New persistent status.</param>
    /// <param name="lastError">Human-readable reason, or <c>null</c>.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    Task<CredentialHealth> RecordAsync(
        CredentialHealthKind kind,
        string subjectId,
        string secretName,
        CredentialHealthStatus status,
        string? lastError,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads the most recent row for <c>(tenant, kind, subjectId, secretName)</c>
    /// or returns <c>null</c> when no validation has been recorded yet.
    /// </summary>
    Task<CredentialHealth?> GetAsync(
        CredentialHealthKind kind,
        string subjectId,
        string secretName,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Enumerates credential-health rows for the current tenant, optionally
    /// filtered by kind. Ordering is implementation-defined.
    /// </summary>
    /// <param name="kind">
    /// Filter by kind, or <c>null</c> to return rows for both runtimes and
    /// connectors.
    /// </param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    Task<IReadOnlyList<CredentialHealth>> ListAsync(
        CredentialHealthKind? kind,
        CancellationToken cancellationToken = default);
}