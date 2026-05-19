// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connector.GitHub.Auth;

using Cvoya.Spring.Core.Secrets;
using Cvoya.Spring.Core.Tenancy;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

/// <summary>
/// Single dispatch for outbound GitHub authentication per ADR-0047 §6.
/// Reads the unit binding once, dispatches on whichever auth field is set,
/// and returns the resolved <see cref="GitHubAuthCredential"/> Octokit call
/// sites consume. There is no chain: the binding-create gate (ADR-0047 §11)
/// guarantees exactly one of <c>AppInstallationId</c> / <c>PatSecretName</c>
/// is set, so the dispatch is a single read.
/// </summary>
/// <remarks>
/// <para>
/// <b>Use-time error surface.</b> When the dispatch cannot produce a
/// credential — secret missing, installation token mint rejected,
/// defensive-assertion fall-through — the resolver raises
/// <see cref="GitHubBindingAuthMissingException"/> carrying the stable
/// <c>GitHubBindingAuthMissing</c> code. Callers translate the exception
/// into whatever wire envelope their endpoint emits; the connector itself
/// just lets it propagate so the dispatcher's failure surface stays clean.
/// </para>
/// <para>
/// <b>Singleton-safety.</b> The resolver is registered as a singleton so
/// every Octokit call site (the webhook-driven label roundtrip, the
/// runtime-context contributor, the PR-files fetcher) takes a constant DI
/// dependency. <see cref="ISecretResolver"/> is scoped, so the resolver
/// captures <see cref="IServiceScopeFactory"/> and creates a fresh scope
/// per call. This keeps the otherwise-cyclic singleton graph
/// (<c>GitHubConnector</c> → resolver → <c>ISecretResolver</c> →
/// <c>SpringDbContext</c> → tenant filter) acyclic at construction time
/// while still honouring ADR-0003's Unit → Tenant fall-through at read
/// time.
/// </para>
/// </remarks>
public class GitHubBindingAuthResolver
{
    private readonly GitHubAppAuth _appAuth;
    private readonly IInstallationTokenCache _tokenCache;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<GitHubBindingAuthResolver> _logger;

    /// <summary>
    /// Initialises the resolver.
    /// </summary>
    /// <param name="appAuth">The GitHub App JWT / installation-token-mint surface.</param>
    /// <param name="tokenCache">Per-installation token cache shared across hosts.</param>
    /// <param name="scopeFactory">
    /// Scope factory used to resolve the scoped
    /// <see cref="ISecretResolver"/> + <see cref="ITenantContext"/> on the
    /// PAT branch. The resolver creates a fresh scope per call so the
    /// singleton lifetime does not capture a scoped service.
    /// </param>
    /// <param name="logger">Logger.</param>
    public GitHubBindingAuthResolver(
        GitHubAppAuth appAuth,
        IInstallationTokenCache tokenCache,
        IServiceScopeFactory scopeFactory,
        ILogger<GitHubBindingAuthResolver> logger)
    {
        ArgumentNullException.ThrowIfNull(appAuth);
        ArgumentNullException.ThrowIfNull(tokenCache);
        ArgumentNullException.ThrowIfNull(scopeFactory);
        ArgumentNullException.ThrowIfNull(logger);
        _appAuth = appAuth;
        _tokenCache = tokenCache;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    /// <summary>
    /// Resolves the outbound credential the supplied binding pushes with.
    /// Dispatches once on the binding's auth fields:
    /// <list type="bullet">
    ///   <item><description>
    ///     <see cref="UnitGitHubConfig.AppInstallationId"/> set → mint an
    ///     installation token against the configured App via
    ///     <see cref="GitHubAppAuth"/>, surfaced through
    ///     <see cref="IInstallationTokenCache"/> so concurrent callers for
    ///     the same installation coalesce on one in-flight mint.
    ///   </description></item>
    ///   <item><description>
    ///     <see cref="UnitGitHubConfig.PatSecretName"/> set → read the
    ///     plaintext value from the tenant secret store via
    ///     <see cref="ISecretResolver"/>. ADR-0003 surfaces the Unit →
    ///     Tenant fall-through automatically.
    ///   </description></item>
    ///   <item><description>
    ///     Both branches fail or neither field is set → raise
    ///     <see cref="GitHubBindingAuthMissingException"/>.
    ///   </description></item>
    /// </list>
    /// </summary>
    /// <param name="binding">The unit's GitHub binding payload.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The resolved credential value.</returns>
    /// <exception cref="GitHubBindingAuthMissingException">
    /// Raised when the binding's pinned credential cannot be materialised.
    /// </exception>
    public virtual async Task<GitHubAuthCredential> ResolveAsync(
        UnitGitHubConfig binding,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(binding);

        var hasApp = binding.AppInstallationId is > 0;
        var hasPat = !string.IsNullOrWhiteSpace(binding.PatSecretName);

        if (hasApp)
        {
            return await ResolveAppInstallationAsync(binding.AppInstallationId!.Value, cancellationToken)
                .ConfigureAwait(false);
        }
        if (hasPat)
        {
            return await ResolvePatAsync(binding.PatSecretName!, cancellationToken)
                .ConfigureAwait(false);
        }

        // Defensive assertion. ADR-0047 §11's binding-create gate
        // (GitHubBindingAuthRequired) prevents the "neither set" row from
        // landing in storage, so reaching this branch is a wiring bug. Log
        // and raise the use-time code so the operator sees the same
        // structured envelope they would have seen at create time.
        _logger.LogError(
            "GitHub binding has neither AppInstallationId nor PatSecretName set. " +
            "The binding-create gate (ADR-0047 §11) should have rejected this row; " +
            "treating as use-time auth-missing and refusing to proceed.");
        throw new GitHubBindingAuthMissingException(
            "GitHub binding has no auth credential pinned. Exactly one of " +
            "AppInstallationId or PatSecretName must be set on the binding " +
            "(ADR-0047 §11).");
    }

    private async Task<GitHubAuthCredential> ResolveAppInstallationAsync(
        long installationId,
        CancellationToken cancellationToken)
    {
        try
        {
            var minted = await _tokenCache
                .GetOrMintAsync(
                    installationId,
                    (id, ct) => _appAuth.MintInstallationTokenAsync(id, ct),
                    cancellationToken)
                .ConfigureAwait(false);

            return new GitHubAuthCredential(
                minted.Token,
                GitHubAuthCredentialKind.AppInstallation,
                minted.ExpiresAt);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (GitHubBindingAuthMissingException)
        {
            // Defensive: should not happen on this path, but if a future
            // overlay throws the same shape, let it bubble unchanged so
            // the caller sees one stable surface.
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "GitHub App installation token mint failed for installation {InstallationId}; " +
                "raising GitHubBindingAuthMissing.",
                installationId);
            throw new GitHubBindingAuthMissingException(
                $"GitHub App installation token mint failed for installation {installationId}. " +
                "The configured App credentials may have been rotated, revoked, or the " +
                "installation may have been removed.",
                ex);
        }
    }

    private async Task<GitHubAuthCredential> ResolvePatAsync(
        string patSecretName,
        CancellationToken cancellationToken)
    {
        // ADR-0047 §5: the PAT lives in the tenant secret store. Read with
        // SecretScope.Tenant + the ambient tenant id so the resolver lands
        // on the row the binding row's name points at. ADR-0003's Unit →
        // Tenant fall-through is automatic via ISecretResolver — when an
        // OSS-style overlay decides to wire a unit-scoped PAT it lights up
        // for free without the connector knowing.
        await using var scope = _scopeFactory.CreateAsyncScope();
        var tenantContext = scope.ServiceProvider.GetRequiredService<ITenantContext>();
        var secretResolver = scope.ServiceProvider.GetRequiredService<ISecretResolver>();

        var resolution = await secretResolver
            .ResolveWithPathAsync(
                new SecretRef(SecretScope.Tenant, tenantContext.CurrentTenantId, patSecretName),
                cancellationToken)
            .ConfigureAwait(false);

        if (resolution.Path == SecretResolvePath.NotFound
            || string.IsNullOrEmpty(resolution.Value))
        {
            _logger.LogWarning(
                "GitHub binding PAT secret '{SecretName}' not found in the tenant secret store; " +
                "raising GitHubBindingAuthMissing.",
                patSecretName);
            throw new GitHubBindingAuthMissingException(
                $"GitHub binding's PAT secret '{patSecretName}' was not found in the " +
                "tenant secret store. Re-run the OAuth flow or paste a fresh PAT " +
                "under the configured secret name.");
        }

        return new GitHubAuthCredential(
            resolution.Value!,
            GitHubAuthCredentialKind.PersonalAccessToken,
            ExpiresAt: null);
    }
}
