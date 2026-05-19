// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connector.GitHub.Auth.OAuth;

/// <summary>
/// Discriminator telling the OAuth callback what initiated the flow per
/// ADR-0047 §13. The OAuth scaffolding is dual-purpose:
///
/// <list type="bullet">
///   <item><description>
///     <see cref="UserIdentitySurface"/> — the caller (portal user-identity
///     page or the <c>spring user identity authorize-github</c> CLI verb)
///     initiated the flow to link their own connector identity. The
///     callback writes the OAuth-issued token as a tenant secret under the
///     binding-scoped naming convention AND populates the calling tenant
///     user's <c>TenantUserConnectorIdentity.username</c> for the GitHub
///     connector from the OAuth user-info response.
///   </description></item>
///   <item><description>
///     <see cref="BindingWizard"/> — the new-unit wizard initiated the
///     flow to mint a PAT-equivalent token before binding the unit. The
///     callback writes the OAuth-issued token as a tenant secret under
///     the binding's pre-minted UUID, then returns the secret name in the
///     handoff payload so the wizard can pre-fill <c>pat_secret_name</c>
///     on the binding-create call. The wizard owns the row's lifecycle;
///     the OAuth flow only mints the credential it points at.
///   </description></item>
/// </list>
///
/// The <see cref="Unspecified"/> value is the legacy default — used when
/// the caller does not declare an intent (e.g. the read-only "Link GitHub"
/// flow that pre-dates ADR-0047 and only powers <c>list-repositories</c>).
/// </summary>
public enum OAuthInitiationIntent
{
    /// <summary>
    /// Pre-ADR-0047 default — no token persistence, no identity update.
    /// The callback still issues the OAuth session metadata; the resulting
    /// session id powers the existing <c>list-repositories</c> path that
    /// scopes the wizard's repo dropdown to the caller's GitHub identity.
    /// </summary>
    Unspecified = 0,

    /// <summary>
    /// User initiated from the identity surface — link their personal
    /// GitHub account. Side effects: persist the token as a tenant secret
    /// and upsert the calling <c>TenantUser</c>'s GitHub
    /// <c>username</c> / <c>display_handle</c>.
    /// </summary>
    UserIdentitySurface = 1,

    /// <summary>
    /// User initiated from the new-unit wizard — mint a PAT for the
    /// binding being created. Side effects: persist the token as a tenant
    /// secret under the wizard's pre-minted binding-id; return the secret
    /// name so the wizard can wire <c>pat_secret_name</c>.
    /// </summary>
    BindingWizard = 2,
}

/// <summary>
/// Typed payload describing what initiated an OAuth flow, propagated from
/// <c>POST /oauth/authorize</c> through the state store into the callback
/// (ADR-0047 §13). Carries the calling <c>TenantUser</c>'s UUID so the
/// callback knows whose <c>TenantUserConnectorIdentity</c> to refresh, and
/// the wizard-minted binding UUID so the secret-name convention from
/// ADR-0047 §5 lands deterministically.
/// </summary>
/// <param name="Intent">
/// The flow's purpose; see <see cref="OAuthInitiationIntent"/>.
/// </param>
/// <param name="TenantUserId">
/// The calling tenant user's stable UUID. Set for
/// <see cref="OAuthInitiationIntent.UserIdentitySurface"/> and
/// <see cref="OAuthInitiationIntent.BindingWizard"/>; <c>null</c> for the
/// legacy unspecified path. Used by the callback to address the row whose
/// <c>username</c> should be auto-populated.
/// </param>
/// <param name="BindingId">
/// The wizard-minted binding UUID. Set for
/// <see cref="OAuthInitiationIntent.BindingWizard"/> only — the secret is
/// written under <c>binding/&lt;bindingId-no-dash&gt;/github/pat</c> per
/// ADR-0047 §5, and the wizard then creates the binding with the same id.
/// For the user-identity flow the persister generates a transient binding
/// UUID internally (the identity surface has no real binding to address).
/// </param>
public sealed record OAuthInitiationContext(
    OAuthInitiationIntent Intent,
    Guid? TenantUserId,
    Guid? BindingId);
