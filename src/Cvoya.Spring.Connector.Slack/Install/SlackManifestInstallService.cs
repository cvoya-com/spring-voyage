// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connector.Slack.Install;

using System;
using System.Collections.Generic;
using System.Net.Http;

using Cvoya.Spring.Connector.Slack.Auth.OAuth;
using Cvoya.Spring.Connector.Slack.Provisioning;
using Cvoya.Spring.Core.Secrets;
using Cvoya.Spring.Core.Tenancy;

using Microsoft.Extensions.Logging;

/// <summary>
/// Default <see cref="ISlackManifestInstallService"/> implementation.
/// Scoped — it depends on the request-scoped secret store / registry /
/// tenant context, and is resolved per request by
/// <see cref="SlackInstallEndpoints"/>.
///
/// <para>
/// Persistence mirrors <c>SlackInstallStore.PersistInstallAsync</c>: each
/// secret value is written to <see cref="ISecretStore"/> and registered
/// under a tenant-scoped <see cref="SecretRef"/> via
/// <see cref="ISecretRegistry"/>. Writes are all-or-nothing — a failure
/// part-way through rolls back every secret already written this call, so
/// the tenant-secret store never ends up half-populated (the same contract
/// the CLI's <c>--write-tenant-secrets</c> path honours, issue #2839).
/// </para>
/// </summary>
public class SlackManifestInstallService(
    IHttpClientFactory httpClientFactory,
    ISlackOAuthService oauthService,
    ISecretStore secretStore,
    ISecretRegistry secretRegistry,
    ITenantContext tenantContext,
    ILoggerFactory loggerFactory) : ISlackManifestInstallService
{
    /// <summary>
    /// Named <see cref="HttpClient"/> the Manifest-API calls flow through.
    /// Registered in <c>AddCvoyaSpringConnectorSlack</c>. Deliberately NOT
    /// wired through the credential-health watchdog (§15): the
    /// configuration token is a short-lived, operator-supplied per-request
    /// input, not a stored credential whose health we track.
    /// </summary>
    public const string HttpClientName = "slack-manifest-api";

    private readonly ILogger<SlackManifestInstallService> _logger =
        loggerFactory.CreateLogger<SlackManifestInstallService>();

    /// <inheritdoc />
    public async Task<SlackManifestInstallResult> InstallAsync(
        SlackManifestInstallRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var manifestJson = SlackAppManifest.BuildJson(new SlackAppManifest.Inputs(
            AppName: request.AppName,
            SvHost: request.SvHost,
            SocketModeEnabled: request.SocketMode));

        // Dry-run: the portal's manifest preview. No network, no persistence.
        if (request.DryRun)
        {
            return new SlackManifestInstallResult(
                ManifestJson: manifestJson,
                DryRun: true,
                AppId: null,
                AuthorizeUrl: null,
                State: null,
                WrittenSecretNames: Array.Empty<string>());
        }

        var configToken = request.ConfigToken
            ?? throw new InvalidOperationException(
                "A Slack configuration token is required for a non-dry-run install.");

        var http = httpClientFactory.CreateClient(HttpClientName);
        var slackClient = new SlackManifestApiClient(http);

        await slackClient.ValidateAsync(manifestJson, configToken, cancellationToken).ConfigureAwait(false);
        var createResult = await slackClient.CreateAsync(manifestJson, configToken, cancellationToken)
            .ConfigureAwait(false);

        var redirectUri = request.SvHost.TrimEnd('/') + SlackAppManifest.OAuthCallbackPath;
        var credentials = new SlackProvisionedCredentials(
            AppId: createResult.AppId,
            ClientId: createResult.Credentials!.ClientId,
            ClientSecret: createResult.Credentials.ClientSecret!,
            SigningSecret: createResult.Credentials.SigningSecret!,
            VerificationToken: createResult.Credentials.VerificationToken,
            RedirectUri: redirectUri);

        var mapping = SlackCredentialSecretMapper.BuildSecretPairs(credentials);
        var written = await PersistTenantSecretsAsync(mapping.Pairs, cancellationToken).ConfigureAwait(false);

        _logger.LogInformation(
            "Provisioned Slack app {AppId} for tenant {TenantId}; persisted {SecretCount} tenant secrets.",
            createResult.AppId, tenantContext.CurrentTenantId, written.Count);

        // The connector resolves OAuth config per call (tenant → platform
        // → env), so the secrets we just wrote are immediately visible.
        // Mint a state-bearing consent URL — Slack's
        // manifest.create.oauth_authorize_url carries no state token,
        // which the SV callback rejects ("Both 'code' and 'state' …").
        var authorize = await oauthService.BeginAuthorizationAsync(request.ClientState, cancellationToken)
            .ConfigureAwait(false);

        return new SlackManifestInstallResult(
            ManifestJson: manifestJson,
            DryRun: false,
            AppId: createResult.AppId,
            AuthorizeUrl: authorize.AuthorizeUrl,
            State: authorize.State,
            WrittenSecretNames: written);
    }

    /// <summary>
    /// Writes each pair as a tenant-scoped, platform-owned secret. On any
    /// failure, rolls back every secret already written this call
    /// (best-effort) before re-throwing the original error — so the
    /// tenant-secret store never ends up half-populated (issue #2839).
    /// </summary>
    private async Task<IReadOnlyList<string>> PersistTenantSecretsAsync(
        IReadOnlyList<SlackCredentialSecretMapper.SecretPair> pairs,
        CancellationToken cancellationToken)
    {
        var tenantId = tenantContext.CurrentTenantId;
        var committed = new List<(SecretRef Ref, string StoreKey)>();

        // A store blob written but not yet registered: if the register
        // throws, this orphaned blob has no registry row, so it must be
        // cleaned up alongside the fully-committed secrets.
        string? pendingStoreKey = null;

        try
        {
            foreach (var pair in pairs)
            {
                var secretRef = new SecretRef(SecretScope.Tenant, tenantId, pair.Name);
                pendingStoreKey = await secretStore.WriteAsync(pair.Value, cancellationToken).ConfigureAwait(false);
                await secretRegistry.RegisterAsync(secretRef, pendingStoreKey, SecretOrigin.PlatformOwned, cancellationToken)
                    .ConfigureAwait(false);
                committed.Add((secretRef, pendingStoreKey));
                pendingStoreKey = null;
            }
        }
        catch
        {
            // Clean up the in-flight blob (written but not registered)
            // first, then every fully-committed prior secret. Delete the
            // store slot before the registry row, mirroring
            // SlackInstallStore's delete order. Best-effort; the original
            // error is what the operator must see, so we always re-throw.
            if (pendingStoreKey is not null)
            {
                try
                {
                    await secretStore.DeleteAsync(pendingStoreKey, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex,
                        "Best-effort cleanup of an in-flight Slack secret blob failed during install rollback.");
                }
            }

            foreach (var (secretRef, storeKey) in committed)
            {
                try
                {
                    await secretStore.DeleteAsync(storeKey, CancellationToken.None).ConfigureAwait(false);
                    await secretRegistry.DeleteAsync(secretRef, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex,
                        "Best-effort rollback of tenant secret '{SecretName}' failed during a Slack install abort.",
                        secretRef.Name);
                }
            }
            throw;
        }

        return committed.ConvertAll(c => c.Ref.Name);
    }
}
