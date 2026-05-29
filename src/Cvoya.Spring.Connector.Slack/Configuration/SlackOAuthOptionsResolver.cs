// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connector.Slack.Configuration;

using Cvoya.Spring.Connector.Slack.Provisioning;
using Cvoya.Spring.Core.Secrets;
using Cvoya.Spring.Core.Tenancy;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

/// <summary>
/// Default <see cref="ISlackOAuthOptionsResolver"/> implementation.
/// Singleton; reads scoped <see cref="ISecretResolver"/> +
/// <see cref="ITenantContext"/> through an
/// <see cref="IServiceScopeFactory"/> per call, mirroring the
/// safety pattern <c>SlackInstallStore</c> uses.
///
/// <para>
/// Resolution chain per credential field: tenant-scoped secret →
/// platform-scoped secret → env-config field on
/// <see cref="SlackOAuthOptions"/>. See
/// <see cref="ISlackOAuthOptionsResolver"/> for the full contract and
/// the rationale (issue #2849).
/// </para>
/// </summary>
public class SlackOAuthOptionsResolver : ISlackOAuthOptionsResolver
{
    /// <summary>
    /// Well-known tenant- / platform-secret names for the four credential
    /// fields the resolver consumes. These alias the canonical
    /// <see cref="SlackSecretNames"/> constants in the shared provisioning
    /// kernel, so the resolution path stays in lock-step with every write
    /// surface — the <c>spring connector slack install</c> CLI verb and
    /// the portal's server-side install endpoint both persist under these
    /// exact names (issue #2849 / #2882).
    /// </summary>
    public static class SecretNames
    {
        public const string ClientId = SlackSecretNames.ClientId;
        public const string ClientSecret = SlackSecretNames.ClientSecret;
        public const string SigningSecret = SlackSecretNames.SigningSecret;
        public const string RedirectUri = SlackSecretNames.RedirectUri;
    }

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IOptionsMonitor<SlackOAuthOptions> _envOptions;
    private readonly ILogger<SlackOAuthOptionsResolver> _logger;

    /// <summary>Creates a new <see cref="SlackOAuthOptionsResolver"/>.</summary>
    public SlackOAuthOptionsResolver(
        IServiceScopeFactory scopeFactory,
        IOptionsMonitor<SlackOAuthOptions> envOptions,
        ILoggerFactory loggerFactory)
    {
        ArgumentNullException.ThrowIfNull(scopeFactory);
        ArgumentNullException.ThrowIfNull(envOptions);
        ArgumentNullException.ThrowIfNull(loggerFactory);

        _scopeFactory = scopeFactory;
        _envOptions = envOptions;
        _logger = loggerFactory.CreateLogger<SlackOAuthOptionsResolver>();
    }

    /// <inheritdoc />
    public async Task<SlackOAuthOptions> ResolveAsync(CancellationToken cancellationToken)
    {
        var env = _envOptions.CurrentValue;

        await using var scope = _scopeFactory.CreateAsyncScope();
        var sp = scope.ServiceProvider;
        var resolver = sp.GetRequiredService<ISecretResolver>();
        var tenantContext = sp.GetRequiredService<ITenantContext>();
        var tenantId = tenantContext.CurrentTenantId;

        var clientId = await ResolveFieldAsync(resolver, tenantId, SecretNames.ClientId, env.ClientId, cancellationToken);
        var clientSecret = await ResolveFieldAsync(resolver, tenantId, SecretNames.ClientSecret, env.ClientSecret, cancellationToken);
        var signingSecret = await ResolveFieldAsync(resolver, tenantId, SecretNames.SigningSecret, env.SigningSecret, cancellationToken);
        var redirectUri = await ResolveFieldAsync(resolver, tenantId, SecretNames.RedirectUri, env.RedirectUri, cancellationToken);

        return new SlackOAuthOptions
        {
            ClientId = clientId,
            ClientSecret = clientSecret,
            SigningSecret = signingSecret,
            RedirectUri = redirectUri,
            // Non-credential fields: env-config only — no per-tenant
            // override path. Scopes are install-time and tied to the
            // app manifest; StateTtl is an operational tunable.
            Scopes = env.Scopes,
            StateTtl = env.StateTtl,
        };
    }

    private async Task<string> ResolveFieldAsync(
        ISecretResolver resolver,
        Guid tenantId,
        string secretName,
        string envFallback,
        CancellationToken cancellationToken)
    {
        // 1. Tenant scope.
        var tenantRef = new SecretRef(SecretScope.Tenant, tenantId, secretName);
        var tenantValue = await resolver.ResolveAsync(tenantRef, cancellationToken);
        if (!string.IsNullOrEmpty(tenantValue))
        {
            return tenantValue;
        }

        // 2. Platform scope.
        var platformRef = new SecretRef(SecretScope.Platform, null, secretName);
        var platformValue = await resolver.ResolveAsync(platformRef, cancellationToken);
        if (!string.IsNullOrEmpty(platformValue))
        {
            return platformValue;
        }

        // 3. Env-config fallback. Empty string is the env-config
        // "unset" sentinel — SlackOAuthService's EnsureConfigured
        // helper surfaces the operator-facing error message.
        if (!string.IsNullOrEmpty(envFallback))
        {
            _logger.LogDebug(
                "Slack OAuth field '{SecretName}' resolved from env-config (no tenant / platform secret).",
                secretName);
        }
        return envFallback;
    }
}
