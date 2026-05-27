// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connector.Slack.Auth.OAuth;

/// <summary>
/// Slack OAuth + binding lifecycle service (ADR-0061 §2.3 / §2.5).
/// Owns the cryptographic state, the <c>oauth.v2.access</c> exchange,
/// the Enterprise Grid detection probe, secret persistence, and the
/// disconnect <c>auth.revoke</c> call. The implementation lives in
/// <see cref="SlackOAuthService"/>; the interface exists so the
/// endpoint surface and tests can drive a substitute.
/// </summary>
public interface ISlackOAuthService
{
    /// <summary>
    /// Begins an OAuth authorization flow. Generates a fresh
    /// cryptographic state, persists it, and returns the Slack
    /// <c>oauth/v2/authorize</c> URL the operator's browser must visit.
    /// </summary>
    Task<SlackAuthorizeResult> BeginAuthorizationAsync(
        string? clientState,
        CancellationToken cancellationToken);

    /// <summary>
    /// Handles the OAuth callback — consumes the state, exchanges the
    /// authorization <paramref name="code"/> via
    /// <c>oauth.v2.access</c>, runs the Enterprise Grid probe via
    /// <c>team.info</c>, and (on success) persists the tenant binding,
    /// workspace-map row, and tenant secrets.
    /// </summary>
    Task<SlackCallbackOutcome> HandleCallbackAsync(
        string code,
        string state,
        CancellationToken cancellationToken);

    /// <summary>
    /// Disconnects the current tenant from Slack — calls
    /// <c>auth.revoke</c> against the bot token, then deletes the
    /// binding row, the workspace-map row, and the tenant secrets.
    /// </summary>
    Task<SlackDisconnectOutcome> DisconnectAsync(CancellationToken cancellationToken);
}

/// <summary>Result of <see cref="ISlackOAuthService.BeginAuthorizationAsync"/>.</summary>
public record SlackAuthorizeResult(string AuthorizeUrl, string State);

/// <summary>
/// Tagged union of possible outcomes from
/// <see cref="ISlackOAuthService.HandleCallbackAsync"/>.
/// </summary>
public abstract record SlackCallbackOutcome
{
    /// <summary>
    /// The OAuth exchange succeeded and the binding was persisted.
    /// </summary>
    public sealed record Success(string TeamId, string BotUserId, string InstallerUserId) : SlackCallbackOutcome;

    /// <summary>
    /// The Slack workspace is part of an Enterprise Grid (ADR-0061
    /// §2.3 / §7.6) — the binding is refused. No row is persisted.
    /// </summary>
    public sealed record EnterpriseGridUnsupported(string EnterpriseId, string Reason) : SlackCallbackOutcome;

    /// <summary>
    /// The OAuth callback arrived with a <c>team_id</c> different
    /// from the one the tenant is already bound to (ADR-0061 §2.5 —
    /// "one workspace per install"). The pre-existing binding is left
    /// untouched.
    /// </summary>
    public sealed record WorkspaceConflict(
        string ExpectedTeamId,
        string ReceivedTeamId,
        string Reason) : SlackCallbackOutcome;

    /// <summary>The OAuth state was unknown / consumed / expired.</summary>
    public sealed record InvalidState : SlackCallbackOutcome;

    /// <summary>
    /// The <c>oauth.v2.access</c> call (or the subsequent
    /// <c>team.info</c> probe) failed at the transport / Slack-API
    /// level. The binding was not persisted.
    /// </summary>
    public sealed record ExchangeFailed(string Reason) : SlackCallbackOutcome;
}

/// <summary>
/// Tagged union of possible outcomes from
/// <see cref="ISlackOAuthService.DisconnectAsync"/>.
/// </summary>
public abstract record SlackDisconnectOutcome
{
    /// <summary>The binding (and its secrets) were removed.</summary>
    public sealed record Removed : SlackDisconnectOutcome;

    /// <summary>No binding existed for the current tenant.</summary>
    public sealed record NotBound : SlackDisconnectOutcome;

    /// <summary>
    /// The remote <c>auth.revoke</c> call failed. The local binding
    /// and secrets are deleted regardless (best-effort revoke).
    /// </summary>
    public sealed record RevokeFailed(string Reason) : SlackDisconnectOutcome;
}
