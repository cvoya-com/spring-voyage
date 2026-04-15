// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connector.GitHub.Auth.OAuth;

/// <summary>
/// Maps a session id → <see cref="OAuthSession"/> record (non-secret
/// metadata + <see cref="ISecretStore"/> keys). The default OSS
/// implementation is a per-host <see cref="System.Collections.Concurrent.ConcurrentDictionary{TKey,TValue}"/>;
/// the cloud repo overrides with a tenant-scoped persistent store.
///
/// <para>
/// This abstraction is deliberately separate from <see cref="ISecretStore"/>:
/// the secret store gives us encryption-at-rest for the token plaintext,
/// but session lookups need structured metadata (login, scopes, expiry)
/// that does not belong in a plaintext-opaque blob. Keeping them split
/// means the audit decorator on <see cref="Cvoya.Spring.Core.Secrets.ISecretResolver"/>
/// sees every token read, but routine "show the user which account is
/// linked" lookups do not.
/// </para>
/// </summary>
public interface IOAuthSessionStore
{
    /// <summary>Persists a session record, keyed by <see cref="OAuthSession.SessionId"/>.</summary>
    Task SaveAsync(OAuthSession session, CancellationToken ct);

    /// <summary>Retrieves a session by id, or <c>null</c> if unknown.</summary>
    Task<OAuthSession?> GetAsync(string sessionId, CancellationToken ct);

    /// <summary>Deletes a session. Missing ids are not errors.</summary>
    Task DeleteAsync(string sessionId, CancellationToken ct);
}