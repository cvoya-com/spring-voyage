// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connector.GitHub.Auth.OAuth;

using Cvoya.Spring.Core.Secrets;
using Cvoya.Spring.Core.Security;
using Cvoya.Spring.Core.Tenancy;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

/// <summary>
/// Default <see cref="IOAuthTokenPersister"/>. Writes the OAuth-issued
/// access token to the tenant secret store under the binding-scoped
/// naming convention from ADR-0047 §5 and, for the user-identity
/// initiation path, refreshes the calling tenant user's
/// <c>TenantUserConnectorIdentity</c> for the GitHub connector.
/// </summary>
/// <remarks>
/// <para>
/// <b>Singleton + scoped consumers.</b> The persister is registered as a
/// singleton so the OAuth service (also singleton) can take a constant
/// dependency. <see cref="ISecretResolver"/>'s peers
/// (<see cref="ISecretRegistry"/>, <see cref="ITenantContext"/>,
/// <see cref="ITenantUserConnectorIdentityWriter"/>) are scoped — the
/// persister captures <see cref="IServiceScopeFactory"/> and creates a
/// fresh scope per call, matching the singleton-safety pattern
/// <see cref="Cvoya.Spring.Connector.GitHub.Auth.GitHubBindingAuthResolver"/>
/// already uses for the PAT-read branch.
/// </para>
/// </remarks>
public sealed class OAuthTokenPersister : IOAuthTokenPersister
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<OAuthTokenPersister> _logger;

    /// <summary>Initialises the persister.</summary>
    public OAuthTokenPersister(
        IServiceScopeFactory scopeFactory,
        ILogger<OAuthTokenPersister> logger)
    {
        ArgumentNullException.ThrowIfNull(scopeFactory);
        ArgumentNullException.ThrowIfNull(logger);
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<OAuthTokenPersistOutcome> PersistAsync(
        string accessToken,
        GitHubUserIdentity userIdentity,
        OAuthInitiationContext? initiation,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(accessToken);
        ArgumentNullException.ThrowIfNull(userIdentity);

        var intent = initiation?.Intent ?? OAuthInitiationIntent.Unspecified;
        if (intent == OAuthInitiationIntent.Unspecified)
        {
            // Legacy / list-repositories-only flow: do not write a tenant
            // secret and do not touch the identity row. The OAuth service
            // still records the session metadata for the
            // user-scoped repo filter (`list-repositories`).
            return new OAuthTokenPersistOutcome(
                OAuthTokenPersistKind.Skipped,
                PatSecretName: null,
                BindingId: null,
                IdentityOutcome: null);
        }

        // ADR-0047 §13 option (a): the wizard pre-mints the binding UUID
        // and supplies it through the initiation context. For the
        // user-identity surface no binding row exists yet — we mint a
        // transient UUID so the §5 naming convention still applies, and
        // the operator-facing CLI / portal surface relays the name back
        // for any later binding-create call.
        var bindingId = initiation?.BindingId ?? Guid.NewGuid();
        var secretName = BuildBindingSecretName(bindingId);

        await using var scope = _scopeFactory.CreateAsyncScope();
        var sp = scope.ServiceProvider;
        var tenantContext = sp.GetRequiredService<ITenantContext>();
        var secretStore = sp.GetRequiredService<ISecretStore>();
        var secretRegistry = sp.GetRequiredService<ISecretRegistry>();

        // Write the access-token plaintext to the store first; the
        // registry insert addresses the resulting opaque store key, never
        // the plaintext. Ordering matters: a registry row pointing at a
        // missing store key would surface as a `NotFound` resolve at
        // read time.
        var storeKey = await secretStore.WriteAsync(accessToken, cancellationToken).ConfigureAwait(false);

        var secretRef = new SecretRef(
            SecretScope.Tenant,
            tenantContext.CurrentTenantId,
            secretName);

        // RegisterAsync atomically replaces any prior chain for the same
        // (scope, owner, name). Re-running the OAuth flow under the same
        // binding-id (e.g. after a wizard refresh) overwrites cleanly
        // rather than accumulating versions.
        await secretRegistry.RegisterAsync(
            secretRef,
            storeKey,
            SecretOrigin.PlatformOwned,
            cancellationToken).ConfigureAwait(false);

        _logger.LogInformation(
            "OAuth-issued GitHub PAT persisted under tenant secret '{SecretName}' " +
            "(binding {BindingId}, intent {Intent})",
            secretName, bindingId, intent);

        // For the user-identity surface, refresh the calling tenant
        // user's GitHub display identity from the OAuth user-info
        // response — the UX nicety ADR-0047 §13 (2) describes.
        TenantUserConnectorIdentityUpsertOutcome? identityOutcome = null;
        if (intent == OAuthInitiationIntent.UserIdentitySurface
            && initiation?.TenantUserId is Guid tenantUserId
            && tenantUserId != Guid.Empty
            && !string.IsNullOrWhiteSpace(userIdentity.Login))
        {
            var writer = sp.GetRequiredService<ITenantUserConnectorIdentityWriter>();
            identityOutcome = await writer.UpsertAsync(
                tenantUserId,
                ConnectorSlugs.GitHub,
                userIdentity.Login,
                displayHandle: string.IsNullOrWhiteSpace(userIdentity.Name) ? null : userIdentity.Name,
                cancellationToken).ConfigureAwait(false);

            if (identityOutcome == TenantUserConnectorIdentityUpsertOutcome.Upserted)
            {
                _logger.LogInformation(
                    "Refreshed TenantUserConnectorIdentity for tenant user {TenantUserId} " +
                    "to GitHub login '{Login}' from OAuth user-info",
                    tenantUserId, userIdentity.Login);
            }
            else
            {
                _logger.LogWarning(
                    "TenantUserConnectorIdentity refresh for tenant user {TenantUserId} " +
                    "on GitHub login '{Login}' returned {Outcome}; the OAuth token " +
                    "remains persisted under '{SecretName}'.",
                    tenantUserId, userIdentity.Login, identityOutcome, secretName);
            }
        }

        return new OAuthTokenPersistOutcome(
            OAuthTokenPersistKind.Persisted,
            secretName,
            bindingId,
            identityOutcome);
    }

    /// <summary>
    /// Builds the ADR-0047 §5 binding-scoped secret name for a GitHub
    /// PAT: <c>binding/&lt;binding-id-no-dash&gt;/github/pat</c>.
    /// Centralised so the wizard / portal can call the same helper at
    /// pre-mint time if they need to display the prospective secret
    /// name in the UI.
    /// </summary>
    /// <param name="bindingId">The binding UUID.</param>
    public static string BuildBindingSecretName(Guid bindingId) =>
        $"binding/{bindingId:N}/{ConnectorSlugs.GitHub}/pat";

    private static class ConnectorSlugs
    {
        public const string GitHub = "github";
    }
}
