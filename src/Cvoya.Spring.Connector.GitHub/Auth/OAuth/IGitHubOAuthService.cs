// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connector.GitHub.Auth.OAuth;

/// <summary>
/// Orchestrates the OAuth flow. Composes <see cref="IOAuthStateStore"/>,
/// <see cref="IGitHubOAuthHttpClient"/>, <see cref="IOAuthSessionStore"/>,
/// <see cref="Cvoya.Spring.Core.Secrets.ISecretStore"/>, and
/// <see cref="IOAuthTokenPersister"/> so the endpoint layer stays a thin
/// HTTP shell.
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
    /// <param name="initiation">
    /// Typed payload declaring what initiated the flow per ADR-0047 §13.
    /// Stored alongside the pending state entry and surfaced on the
    /// callback so the persister can drive the binding-id-aware secret
    /// write and the optional identity refresh. <c>null</c> falls
    /// through as <see cref="OAuthInitiationIntent.Unspecified"/> — the
    /// legacy session-only flow that powers the wizard's
    /// <c>list-repositories</c> dropdown.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    Task<AuthorizeResult> BeginAuthorizationAsync(
        IReadOnlyList<string>? scopesOverride,
        string? clientState,
        OAuthInitiationContext? initiation,
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
/// <param name="PatSecretName">
/// Tenant-scoped secret name the OAuth-issued token was persisted under
/// per ADR-0047 §5, or <c>null</c> for the legacy flow that does not
/// persist a binding-usable PAT. The wizard reads this through the
/// browser handoff to pre-fill <c>pat_secret_name</c> on the
/// subsequent binding-create call.
/// </param>
/// <param name="BindingId">
/// Binding UUID the secret is addressed by, or <c>null</c> for the
/// legacy flow. For the wizard intent this matches the UUID the wizard
/// pre-minted on the authorize call (so the wizard's binding-create
/// reuses the same id); for the user-identity intent the persister
/// generates a transient UUID purely so the §5 secret-name shape is
/// uniform.
/// </param>
public record CallbackResult(
    string? SessionId,
    string? Login,
    string? Error,
    string? ErrorDescription,
    string? PatSecretName = null,
    Guid? BindingId = null);
