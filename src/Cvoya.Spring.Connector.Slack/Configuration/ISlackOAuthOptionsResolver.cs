// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connector.Slack.Configuration;

/// <summary>
/// Per-call resolver for <see cref="SlackOAuthOptions"/>. The Slack
/// connector's OAuth surface (<c>SlackOAuthService</c>) consumes this
/// shape instead of <c>IOptionsMonitor&lt;SlackOAuthOptions&gt;</c>
/// directly so OAuth credentials can be sourced from per-tenant
/// secrets (issue #2849).
///
/// <para>
/// <b>Resolution order</b>, per credential field
/// (<see cref="SlackOAuthOptions.ClientId"/>,
/// <see cref="SlackOAuthOptions.ClientSecret"/>,
/// <see cref="SlackOAuthOptions.SigningSecret"/>,
/// <see cref="SlackOAuthOptions.RedirectUri"/>):
/// </para>
///
/// <list type="number">
///   <item><description><b>Tenant-scoped secret</b> at the well-known name (e.g. <c>slack-oauth-client-id</c>). Persisted by <c>spring connector slack install --write-tenant-secrets</c>.</description></item>
///   <item><description><b>Platform-scoped secret</b> at the same name. Persisted by <c>spring connector slack install --write-secrets</c>.</description></item>
///   <item><description><b>Env-config field</b> bound from <c>Slack:OAuth:*</c>. Populated by <c>spring connector slack install --write-env</c> or hand-edited <c>spring.env</c>.</description></item>
/// </list>
///
/// <para>
/// Non-credential fields (<see cref="SlackOAuthOptions.Scopes"/>,
/// <see cref="SlackOAuthOptions.StateTtl"/>) are sourced only from the
/// env-config options — they are not credentials and have no per-tenant
/// override path.
/// </para>
///
/// <para>
/// The resolver implementation is registered as a singleton (the
/// consuming <c>SlackOAuthService</c> is also a singleton) and reads
/// scoped <c>ISecretResolver</c> + <c>ITenantContext</c> through an
/// <c>IServiceScopeFactory</c> per call — the same pattern
/// <c>SlackInstallStore</c> uses.
/// </para>
/// </summary>
public interface ISlackOAuthOptionsResolver
{
    /// <summary>
    /// Resolves a fresh <see cref="SlackOAuthOptions"/> snapshot with the
    /// credential fields filled from the tenant → platform → env-config
    /// precedence chain. Non-credential fields are copied from the
    /// env-config options as-is.
    /// </summary>
    Task<SlackOAuthOptions> ResolveAsync(CancellationToken cancellationToken);
}
