// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connector.GitHub.Auth.OAuth;

/// <summary>
/// Persists the OAuth-issued access token as a tenant secret under the
/// binding-scoped naming convention from ADR-0047 §5
/// (<c>binding/&lt;binding-id-no-dash&gt;/&lt;connector-slug&gt;/pat</c>),
/// and — for the user-identity initiation path of ADR-0047 §13 —
/// optionally refreshes the calling
/// <c>TenantUserConnectorIdentity.username</c> from the OAuth user-info
/// response.
/// </summary>
/// <remarks>
/// <para>
/// The persister is invoked from the OAuth callback (per ADR-0047 §13)
/// after the code-for-token exchange has succeeded and the GitHub
/// user-info response has been resolved. The OAuth scaffolding still
/// owns the session-store entry; the persister adds only the binding-
/// usable secret + identity-refresh side effects on top.
/// </para>
/// <para>
/// <b>Binding-id strategy (ADR-0047 §13 option (a)).</b> The
/// <see cref="OAuthInitiationIntent.BindingWizard"/> path expects the
/// wizard to pre-mint the binding UUID and supply it through the
/// initiation context; the secret is written under that exact id so the
/// subsequent binding-create call can reference
/// <c>binding/&lt;wizard-minted-id&gt;/github/pat</c> without rewrites.
/// The <see cref="OAuthInitiationIntent.UserIdentitySurface"/> path has
/// no binding row to address — the persister mints a transient binding
/// UUID purely so the secret name stays uniform with ADR-0047 §5; the
/// operator-facing CLI / portal can then surface the name back to the
/// caller for use on a later binding-create call.
/// </para>
/// </remarks>
public interface IOAuthTokenPersister
{
    /// <summary>
    /// Writes the supplied <paramref name="accessToken"/> to the tenant
    /// secret store under the binding-scoped naming convention and, when
    /// <paramref name="initiation"/> requests it, refreshes the calling
    /// tenant user's GitHub display identity. Returns the persisted
    /// secret name so the callback can echo it through the browser
    /// handoff to the wizard.
    /// </summary>
    /// <param name="accessToken">
    /// The plaintext OAuth access token. Treated as opaque; the
    /// persister never logs the value and writes it through
    /// <see cref="Cvoya.Spring.Core.Secrets.ISecretStore.WriteAsync"/>
    /// before the registry insert.
    /// </param>
    /// <param name="userIdentity">
    /// The GitHub user-info response associated with the freshly-minted
    /// token. The persister uses
    /// <see cref="GitHubUserIdentity.Login"/> for the optional
    /// <c>TenantUserConnectorIdentity.username</c> upsert; the
    /// <see cref="GitHubUserIdentity.Name"/> is used as the display
    /// handle when non-empty.
    /// </param>
    /// <param name="initiation">
    /// Typed payload describing what initiated the flow (ADR-0047 §13).
    /// <c>null</c> falls through as
    /// <see cref="OAuthInitiationIntent.Unspecified"/> — the persister
    /// then returns
    /// <see cref="OAuthTokenPersistOutcome.Skipped"/> without writing a
    /// secret or touching identity rows. This keeps the pre-ADR-0047
    /// <c>list-repositories</c>-only flow's side-effect surface
    /// unchanged.
    /// </param>
    /// <param name="cancellationToken">Propagates request cancellation.</param>
    /// <returns>
    /// A summary of what was persisted. The <c>PatSecretName</c> field
    /// is the value the wizard pre-fills <c>pat_secret_name</c> with on
    /// the subsequent binding-create call; <c>null</c> when no secret
    /// was written.
    /// </returns>
    Task<OAuthTokenPersistOutcome> PersistAsync(
        string accessToken,
        GitHubUserIdentity userIdentity,
        OAuthInitiationContext? initiation,
        CancellationToken cancellationToken);
}

/// <summary>
/// Summary of an <see cref="IOAuthTokenPersister.PersistAsync"/> call.
/// </summary>
/// <param name="Outcome">High-level branch the persister took.</param>
/// <param name="PatSecretName">
/// Tenant-scoped secret name the access token was persisted under, or
/// <c>null</c> when no secret was written. Matches the value the wizard
/// passes as <c>pat_secret_name</c> to the binding-create endpoint.
/// </param>
/// <param name="BindingId">
/// Binding UUID the secret is addressed by. For the wizard flow this is
/// the wizard-supplied id; for the identity-surface flow this is a
/// transient id minted by the persister so the §5 naming convention
/// still applies.
/// </param>
/// <param name="IdentityOutcome">
/// Outcome of the optional identity upsert; <c>null</c> when the
/// persister did not attempt to write a tenant-user identity (either
/// the intent did not request it, or the calling
/// <see cref="OAuthInitiationContext.TenantUserId"/> was missing).
/// </param>
public sealed record OAuthTokenPersistOutcome(
    OAuthTokenPersistKind Outcome,
    string? PatSecretName,
    Guid? BindingId,
    Cvoya.Spring.Core.Security.TenantUserConnectorIdentityUpsertOutcome? IdentityOutcome);

/// <summary>
/// High-level branch taken by an <see cref="IOAuthTokenPersister"/> call.
/// </summary>
public enum OAuthTokenPersistKind
{
    /// <summary>
    /// No side effects were applied — the initiation context did not
    /// request token persistence (e.g.
    /// <see cref="OAuthInitiationIntent.Unspecified"/>).
    /// </summary>
    Skipped = 0,

    /// <summary>
    /// The token was written and the result is binding-usable. For the
    /// user-identity surface this also implies the identity upsert was
    /// attempted (see <see cref="OAuthTokenPersistOutcome.IdentityOutcome"/>
    /// for its result).
    /// </summary>
    Persisted = 1,
}
