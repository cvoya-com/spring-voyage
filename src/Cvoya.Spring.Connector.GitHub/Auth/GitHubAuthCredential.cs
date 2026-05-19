// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connector.GitHub.Auth;

/// <summary>
/// The resolved outbound credential a unit's GitHub binding uses for an
/// upstream API call. Produced by
/// <see cref="GitHubBindingAuthResolver.ResolveAsync"/> per ADR-0047 §6.
/// Discriminated by <see cref="Kind"/>: every credential is either an
/// App-installation bearer token (minted via <see cref="GitHubAppAuth"/>
/// against the configured App) or a personal access token (read from the
/// tenant secret store under the binding's
/// <c>pat_secret_name</c>).
/// </summary>
/// <remarks>
/// <para>
/// The value type carries only what call sites need to authenticate Octokit
/// against GitHub. <see cref="ExpiresAt"/> is non-null only on the App
/// branch — the App-installation token-mint returns a server-stamped TTL
/// the runtime-context contributor surfaces to the launched container so
/// the container can plan around it. PAT secrets are operator-managed and
/// rotate out-of-band; the resolver does not synthesise an expiry for them.
/// </para>
/// <para>
/// The two branches share one consumer contract — Octokit's
/// <c>Credentials(token)</c> ctor accepts both shapes — so call sites pass
/// <see cref="Token"/> verbatim regardless of <see cref="Kind"/>. The
/// distinction is preserved for sites that need it (e.g. the runtime
/// contributor's env-var contract emits a <c>TOKEN_EXPIRES_AT</c> only when
/// the App branch fired).
/// </para>
/// </remarks>
/// <param name="Token">The plaintext bearer value Octokit consumes.</param>
/// <param name="Kind">Which branch of the binding-auth dispatch produced the value.</param>
/// <param name="ExpiresAt">
/// Server-stamped expiry for the App branch; <c>null</c> for the PAT branch.
/// </param>
public sealed record GitHubAuthCredential(
    string Token,
    GitHubAuthCredentialKind Kind,
    DateTimeOffset? ExpiresAt);

/// <summary>
/// The two outbound-auth branches per ADR-0047 §6. The binding-create gate
/// (ADR-0047 §11) guarantees exactly one is in play for any given binding;
/// the resolver dispatches on whichever field is set on
/// <see cref="UnitGitHubConfig"/>.
/// </summary>
public enum GitHubAuthCredentialKind
{
    /// <summary>
    /// The binding pinned an <c>AppInstallationId</c>; the resolver minted
    /// (and cached) a short-lived installation access token against the
    /// configured App.
    /// </summary>
    AppInstallation = 0,

    /// <summary>
    /// The binding pinned a <c>PatSecretName</c>; the resolver read the
    /// plaintext PAT from the tenant secret store via
    /// <see cref="Cvoya.Spring.Core.Secrets.ISecretResolver"/>.
    /// </summary>
    PersonalAccessToken = 1,
}
