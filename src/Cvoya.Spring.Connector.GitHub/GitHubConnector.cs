// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connector.GitHub;

using System.Net.Http;
using System.Text;
using System.Text.Json;

using Cvoya.Spring.Connector.GitHub.Auth;
using Cvoya.Spring.Connector.GitHub.Caching;
using Cvoya.Spring.Connector.GitHub.RateLimit;
using Cvoya.Spring.Connector.GitHub.Webhooks;

using Microsoft.Extensions.Logging;

using Octokit;
using Octokit.Internal;

/// <summary>
/// The GitHub connector translates inbound webhook events into domain messages
/// and authenticates outbound GitHub API calls. Tool discovery and invocation
/// live on <see cref="GitHubSkillRegistry"/> (which uses this connector to
/// authenticate).
/// </summary>
public class GitHubConnector : IGitHubConnector
{
    /// <summary>
    /// Name of the <see cref="HttpClient"/> / handler chain this connector
    /// resolves through <see cref="IHttpMessageHandlerFactory"/> for Octokit
    /// repo-API calls. Exposed as a constant so the host can attach the
    /// credential-health watchdog (see <c>CONVENTIONS.md</c> § 16) to the
    /// same logical pipeline.
    /// </summary>
    public const string OctokitHttpClientName = "github-octokit";

    private readonly GitHubAppAuth _auth;
    private readonly GitHubWebhookHandler _webhookHandler;
    private readonly IWebhookSignatureValidator _signatureValidator;
    private readonly GitHubConnectorOptions _options;
    private readonly IGitHubRateLimitTracker _rateLimitTracker;
    private readonly GitHubRetryOptions _retryOptions;
    private readonly IInstallationTokenCache _tokenCache;
    private readonly IGitHubResponseCache _responseCache;
    private readonly IHttpMessageHandlerFactory? _handlerFactory;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger _logger;

    /// <summary>
    /// Initializes the connector. When <paramref name="tokenCache"/> is null
    /// (legacy call sites in tests that don't wire it up) the connector falls
    /// back to a best-effort no-op cache so behaviour stays equivalent to
    /// re-minting on every call.
    /// </summary>
    /// <remarks>
    /// When <paramref name="handlerFactory"/> is supplied, Octokit's inner
    /// HTTP pipeline is sourced from the named handler chain
    /// <see cref="OctokitHttpClientName"/>, which lets the host attach
    /// cross-cutting <see cref="DelegatingHandler"/>s (credential-health
    /// watchdog, proxies) alongside the built-in retry handler without the
    /// connector needing a reference to <c>Cvoya.Spring.Dapr</c>. Left null
    /// (unit tests) the connector falls back to an in-line chain that
    /// wraps the retry handler around a fresh <see cref="HttpClientHandler"/>.
    /// </remarks>
    public GitHubConnector(
        GitHubAppAuth auth,
        GitHubWebhookHandler webhookHandler,
        IWebhookSignatureValidator signatureValidator,
        GitHubConnectorOptions options,
        IGitHubRateLimitTracker rateLimitTracker,
        GitHubRetryOptions retryOptions,
        ILoggerFactory loggerFactory,
        IInstallationTokenCache? tokenCache = null,
        IGitHubResponseCache? responseCache = null,
        IHttpMessageHandlerFactory? handlerFactory = null)
    {
        _auth = auth;
        _webhookHandler = webhookHandler;
        _signatureValidator = signatureValidator;
        _options = options;
        _rateLimitTracker = rateLimitTracker;
        _retryOptions = retryOptions;
        _loggerFactory = loggerFactory;
        _tokenCache = tokenCache ?? new InstallationTokenCache(
            new InstallationTokenCacheOptions(),
            loggerFactory);
        // Default to the no-op cache so legacy constructor call sites
        // (tests) don't need to wire up the response-cache plumbing.
        _responseCache = responseCache ?? NoOpGitHubResponseCache.Instance;
        _handlerFactory = handlerFactory;
        _logger = loggerFactory.CreateLogger<GitHubConnector>();
    }

    /// <summary>
    /// Gets the webhook handler for processing inbound GitHub events.
    /// </summary>
    public IGitHubWebhookHandler WebhookHandler => _webhookHandler;

    /// <summary>
    /// Gets the authentication handler for GitHub App operations.
    /// </summary>
    public GitHubAppAuth Auth => _auth;

    /// <summary>
    /// Processes an incoming webhook payload, validates its signature, and
    /// translates the event into a domain message.
    /// </summary>
    /// <param name="eventType">The GitHub event type from the X-GitHub-Event header.</param>
    /// <param name="payload">The raw webhook payload body.</param>
    /// <param name="signature">The signature from the X-Hub-Signature-256 header.</param>
    /// <returns>
    /// A <see cref="WebhookHandleResult"/> distinguishing invalid-signature,
    /// accepted-but-ignored, and translated-message outcomes so the endpoint
    /// can map each to the correct HTTP status (401 / 202 / 202-with-routing).
    /// </returns>
    public WebhookHandleResult HandleWebhook(string eventType, string payload, string signature)
    {
        if (!_signatureValidator.Validate(payload, signature, _options.WebhookSecret))
        {
            // `eventType` flows in from the X-GitHub-Event header (attacker-
            // controlled); sanitize before logging to prevent log forging.
            _logger.LogWarning(
                "Invalid webhook signature received for event {EventType}",
                SanitizeForLog(eventType));
            return WebhookHandleResult.InvalidSignature;
        }

        using var document = JsonDocument.Parse(payload);
        var message = _webhookHandler.TranslateEvent(eventType, document.RootElement);

        // Compute the invalidation tag set BEFORE disposing the JsonDocument —
        // DeriveInvalidationTags reads from the element. We then return the
        // WebhookHandleResult; the caller records dispatch outcome before the
        // cache is touched by the post-dispatch fire-and-forget below.
        var invalidationTags = _webhookHandler.DeriveInvalidationTags(eventType, document.RootElement);
        if (invalidationTags.Count > 0)
        {
            InvalidateTagsAfterDispatch(eventType, invalidationTags);
        }

        return message is null
            ? WebhookHandleResult.Ignored
            : WebhookHandleResult.Translated(message);
    }

    /// <summary>
    /// Fires cache invalidation calls for every tag <paramref name="tags"/>
    /// carries. Runs on the thread pool so a slow (or failing) cache backend
    /// never blocks the webhook acknowledgement — GitHub retries aggressively
    /// on 5xx / timeouts, so the endpoint must remain fast even if the cache
    /// is degraded. Failures are logged and swallowed.
    /// </summary>
    private void InvalidateTagsAfterDispatch(string eventType, IReadOnlyList<string> tags)
    {
        // Snapshot the cache reference to avoid capturing `this` in the
        // fire-and-forget closure — the invoker isn't expected to outlive
        // the connector, but decoupling the captured state is cheap.
        var cache = _responseCache;
        var logger = _logger;
        // `eventType` flows in from the X-GitHub-Event header and `tag`
        // values are derived from the webhook payload — both attacker-
        // controlled. Pre-sanitize before letting them reach the log stream
        // so a crafted value cannot forge fake log entries via CR/LF or
        // flood logs with oversized strings.
        var safeEventType = SanitizeForLog(eventType);
        _ = Task.Run(async () =>
        {
            foreach (var tag in tags)
            {
                try
                {
                    await cache.InvalidateByTagAsync(tag).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(
                        ex,
                        "Failed to invalidate cache tag {Tag} for event {EventType}; continuing",
                        SanitizeForLog(tag), safeEventType);
                }
            }
        });
    }

    /// <summary>
    /// Strips CR/LF and other control characters from attacker-controlled
    /// values before they reach the log stream, and caps length so a crafted
    /// header or payload field cannot forge fake log entries or flood logs.
    /// Returns "unknown" for null/empty input so log messages remain readable.
    /// </summary>
    private static string SanitizeForLog(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return "unknown";
        }

        const int MaxLogValueLength = 128;
        var length = Math.Min(value.Length, MaxLogValueLength);
        var builder = new StringBuilder(length);
        for (var i = 0; i < length; i++)
        {
            var c = value[i];
            builder.Append(char.IsControl(c) ? '_' : c);
        }

        return builder.ToString();
    }

    /// <summary>
    /// Creates an authenticated <see cref="IGitHubClient"/> for making API calls.
    /// </summary>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>An authenticated GitHub client.</returns>
    public virtual async Task<IGitHubClient> CreateAuthenticatedClientAsync(CancellationToken cancellationToken = default)
    {
        var installationId = _options.InstallationId
            ?? throw new InvalidOperationException("InstallationId must be configured to create an authenticated client.");

        var minted = await _tokenCache.GetOrMintAsync(
            installationId,
            (id, ct) => _auth.MintInstallationTokenAsync(id, ct),
            cancellationToken);

        var client = new GitHubClient(BuildConnection(minted.Token));

        _logger.LogDebug("Created authenticated GitHub client for installation {InstallationId}", installationId);

        return client;
    }

    /// <summary>
    /// Builds the Octokit <see cref="Connection"/> with the rate-limit /
    /// retry <see cref="DelegatingHandler"/> plugged into the underlying
    /// HTTP pipeline. Marked <c>virtual</c> so downstream consumers (e.g. the
    /// cloud repo) can substitute their own pipeline without re-implementing
    /// the whole <see cref="CreateAuthenticatedClientAsync"/> method.
    /// </summary>
    protected virtual IConnection BuildConnection(string token)
    {
        var httpClient = new HttpClientAdapter(CreateHandler);

        return new Connection(
            new ProductHeaderValue("SpringVoyage"),
            GitHubClient.GitHubApiUrl,
            new InMemoryCredentialStore(new Credentials(token)),
            httpClient,
            new SimpleJsonSerializer());
    }

    private HttpMessageHandler CreateHandler()
    {
        // Prefer the host-registered handler chain so the credential-health
        // watchdog (and any future cross-cutting handler attached by the
        // host via AddHttpClient) sits on top of the connector-owned retry
        // handler. IHttpMessageHandlerFactory returns the configured chain
        // for the named client including primary handler + every registered
        // DelegatingHandler, which is what Octokit's HttpClientAdapter
        // expects. The factory manages the handler lifetime (rotation every
        // few minutes), so we must NOT dispose the returned instance — that
        // matches how IHttpClientFactory-sourced clients behave elsewhere.
        if (_handlerFactory is not null)
        {
            return _handlerFactory.CreateHandler(OctokitHttpClientName);
        }

        // Fallback for direct-construction callers (unit tests). Octokit
        // defaults to HttpClientHandler when its caller provides no inner
        // handler; replicate that and wrap it in the retry handler so every
        // outbound request goes through the rate-limit tracker.
        var retryHandler = new GitHubRetryHandler(
            _rateLimitTracker,
            _retryOptions,
            _loggerFactory)
        {
            InnerHandler = new HttpClientHandler(),
        };

        return retryHandler;
    }
}