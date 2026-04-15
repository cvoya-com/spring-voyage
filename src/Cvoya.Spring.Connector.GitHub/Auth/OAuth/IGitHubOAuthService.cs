// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connector.GitHub.Auth.OAuth;

/// <summary>
/// Orchestrates the OAuth flow. Composes <see cref="IOAuthStateStore"/>,
/// <see cref="IGitHubOAuthHttpClient"/>, <see cref="IOAuthSessionStore"/>,
/// and <see cref="Cvoya.Spring.Core.Secrets.ISecretStore"/> so the endpoint
/// layer stays a thin HTTP shell.
/// </summary>
public interface IGitHubOAuthService
{
    /// <summary>
    /// Builds a GitHub authorization URL and persists the associated
    /// <c>state</c> value so the callback can validate it. Returns the
    /// URL the caller should redirect the user to; the state itself lives
    /// on the server for the duration of the authorize window.
    /// </summary>
    /// <param name="scopesOverride">
    /// Per-request scope override. <c>null</c> falls back to the
    /// configured default scopes.
    /// </param>
    /// <param name="clientState">
    /// Opaque state payload the caller wants echoed back on the session.
    /// Never interpreted by the service.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    Task<AuthorizeResult> BeginAuthorizationAsync(
        IReadOnlyList<string>? scopesOverride,
        string? clientState,
        CancellationToken ct);

    /// <summary>
    /// Consumes the callback — validates + deletes the state, exchanges
    /// the code for a token, persists the token in the secret store, and
    /// returns the new session id.
    /// </summary>
    Task<CallbackResult> HandleCallbackAsync(
        string code,
        string state,
        CancellationToken ct);

    /// <summary>
    /// Revokes the GitHub-side grant AND deletes the local session. Returns
    /// <c>false</c> when the session id is unknown (so the caller can surface
    /// 404) and <c>true</c> when the revocation path ran — even if GitHub's
    /// own revocation call returned an unexpected status, we still delete
    /// the local record so the token can't be used.
    /// </summary>
    Task<bool> RevokeAsync(string sessionId, CancellationToken ct);

    /// <summary>
    /// Looks up session metadata without returning the underlying token.
    /// Returns <c>null</c> when the session is unknown.
    /// </summary>
    Task<OAuthSession?> GetSessionAsync(string sessionId, CancellationToken ct);
}

/// <summary>
/// Result of <see cref="IGitHubOAuthService.BeginAuthorizationAsync"/>.
/// </summary>
/// <param name="AuthorizeUrl">The URL the caller should redirect the user to.</param>
/// <param name="State">The server-issued state parameter (for diagnostics / tests).</param>
public record AuthorizeResult(string AuthorizeUrl, string State);

/// <summary>
/// Result of <see cref="IGitHubOAuthService.HandleCallbackAsync"/>. Either
/// <see cref="SessionId"/> is populated (success) or <see cref="Error"/> is
/// populated (failure). The caller maps the failure shape to ProblemDetails.
/// </summary>
/// <param name="SessionId">The newly-issued session id, or <c>null</c> on failure.</param>
/// <param name="Login">The GitHub login of the authorized user, or <c>null</c> on failure.</param>
/// <param name="Error">GitHub-style error code (e.g. <c>invalid_state</c>, <c>access_denied</c>).</param>
/// <param name="ErrorDescription">Human-readable detail of the failure.</param>
public record CallbackResult(
    string? SessionId,
    string? Login,
    string? Error,
    string? ErrorDescription);