// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connector.GitHub;

using System.Net.Http;
using System.Text.Json;

using Cvoya.Spring.Connector.GitHub.Auth;
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
public class GitHubConnector(
    GitHubAppAuth auth,
    GitHubWebhookHandler webhookHandler,
    IWebhookSignatureValidator signatureValidator,
    GitHubConnectorOptions options,
    IGitHubRateLimitTracker rateLimitTracker,
    GitHubRetryOptions retryOptions,
    ILoggerFactory loggerFactory) : IGitHubConnector
{
    private readonly ILogger _logger = loggerFactory.CreateLogger<GitHubConnector>();
    private readonly ILoggerFactory _loggerFactory = loggerFactory;

    /// <summary>
    /// Gets the webhook handler for processing inbound GitHub events.
    /// </summary>
    public IGitHubWebhookHandler WebhookHandler => webhookHandler;

    /// <summary>
    /// Gets the authentication handler for GitHub App operations.
    /// </summary>
    public GitHubAppAuth Auth => auth;

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
        if (!signatureValidator.Validate(payload, signature, options.WebhookSecret))
        {
            _logger.LogWarning("Invalid webhook signature received for event {EventType}", eventType);
            return WebhookHandleResult.InvalidSignature;
        }

        using var document = JsonDocument.Parse(payload);
        var message = webhookHandler.TranslateEvent(eventType, document.RootElement);

        return message is null
            ? WebhookHandleResult.Ignored
            : WebhookHandleResult.Translated(message);
    }

    /// <summary>
    /// Creates an authenticated <see cref="IGitHubClient"/> for making API calls.
    /// </summary>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>An authenticated GitHub client.</returns>
    public virtual async Task<IGitHubClient> CreateAuthenticatedClientAsync(CancellationToken cancellationToken = default)
    {
        var installationId = options.InstallationId
            ?? throw new InvalidOperationException("InstallationId must be configured to create an authenticated client.");

        var token = await auth.GetInstallationTokenAsync(installationId, cancellationToken);

        var client = new GitHubClient(BuildConnection(token));

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
        // Octokit defaults to HttpClientHandler when its caller provides no
        // inner handler; replicate that and wrap it in the retry handler so
        // every outbound request goes through the rate-limit tracker.
        var retryHandler = new GitHubRetryHandler(
            rateLimitTracker,
            retryOptions,
            _loggerFactory)
        {
            InnerHandler = new HttpClientHandler(),
        };

        return retryHandler;
    }
}