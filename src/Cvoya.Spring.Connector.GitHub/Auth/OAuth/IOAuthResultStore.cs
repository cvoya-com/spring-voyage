// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connector.GitHub.Auth.OAuth;

/// <summary>
/// The result of a completed OAuth callback, keyed by the client-minted nonce
/// that was embedded in the authorize request's <c>clientState</c>.
/// </summary>
/// <param name="SessionId">The server-issued session id on success; <c>null</c> on error.</param>
/// <param name="Login">The GitHub login of the authorized user; <c>null</c> on error.</param>
/// <param name="PatSecretName">
/// The tenant-scoped secret name the OAuth token was persisted under per
/// ADR-0047 §5; <c>null</c> for flows that don't persist a binding-usable PAT.
/// </param>
/// <param name="BindingId">
/// Binding UUID (no-dash hex form) the secret is addressed by; <c>null</c>
/// for flows that don't target a specific binding.
/// </param>
/// <param name="Error">Short error code on failure; <c>null</c> on success.</param>
/// <param name="Reason">Human-readable error description on failure; <c>null</c> on success.</param>
public record OAuthCallbackResult(
    string? SessionId,
    string? Login,
    string? PatSecretName,
    string? BindingId,
    string? Error,
    string? Reason);

/// <summary>
/// Stores completed OAuth callback results keyed by the client-minted nonce
/// embedded in the authorize request's <c>clientState</c>. Abstracted so the
/// cloud repo can substitute a distributed backing store for multi-host
/// deployments — see <c>InMemoryOAuthResultStore</c> for the OSS default.
///
/// <para>
/// <b>Single-read.</b> <see cref="Consume"/> removes the entry atomically; a
/// second call for the same nonce returns <c>null</c>.
/// </para>
/// </summary>
public interface IOAuthResultStore
{
    /// <summary>
    /// Stores <paramref name="result"/> keyed by <paramref name="nonce"/>.
    /// No-op when <paramref name="nonce"/> is null or blank.
    /// </summary>
    /// <param name="nonce">The client-minted nonce from <c>clientState</c>.</param>
    /// <param name="result">The callback result to store.</param>
    void Put(string nonce, OAuthCallbackResult result);

    /// <summary>
    /// Atomically retrieves and removes the result for <paramref name="nonce"/>.
    /// Returns <c>null</c> when absent, expired, or <paramref name="nonce"/>
    /// is null/blank.
    /// </summary>
    /// <param name="nonce">The client-minted nonce from <c>clientState</c>.</param>
    /// <returns>The callback result, or <c>null</c> if not found / expired.</returns>
    OAuthCallbackResult? Consume(string nonce);
}
