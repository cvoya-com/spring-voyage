// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connector.GitHub;

using System.Text.Json;
using Cvoya.Spring.Connector.GitHub.Auth;
using Cvoya.Spring.Connector.GitHub.Webhooks;
using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Core.Skills;
using Microsoft.Extensions.Logging;
using Octokit;

/// <summary>
/// The GitHub connector translates inbound webhook events into domain messages
/// and provides outbound skills for interacting with the GitHub API.
/// </summary>
public class GitHubConnector(
    GitHubAppAuth auth,
    GitHubWebhookHandler webhookHandler,
    GitHubSkillRegistry skillRegistry,
    GitHubConnectorOptions options,
    ILoggerFactory loggerFactory)
{
    private readonly ILogger _logger = loggerFactory.CreateLogger<GitHubConnector>();

    /// <summary>
    /// Gets the webhook handler for processing inbound GitHub events.
    /// </summary>
    public GitHubWebhookHandler WebhookHandler => webhookHandler;

    /// <summary>
    /// Gets the skill registry containing all available GitHub tool definitions.
    /// </summary>
    public GitHubSkillRegistry SkillRegistry => skillRegistry;

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
    /// <returns>A domain <see cref="Message"/>, or <c>null</c> if the event is not handled or the signature is invalid.</returns>
    public Message? HandleWebhook(string eventType, string payload, string signature)
    {
        if (!WebhookSignatureValidator.Validate(payload, signature, options.WebhookSecret))
        {
            _logger.LogWarning("Invalid webhook signature received for event {EventType}", eventType);
            return null;
        }

        var jsonPayload = JsonDocument.Parse(payload).RootElement;
        return webhookHandler.TranslateEvent(eventType, jsonPayload);
    }

    /// <summary>
    /// Creates an authenticated <see cref="IGitHubClient"/> for making API calls.
    /// </summary>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>An authenticated GitHub client.</returns>
    public async Task<IGitHubClient> CreateAuthenticatedClientAsync(CancellationToken cancellationToken = default)
    {
        var installationId = options.InstallationId
            ?? throw new InvalidOperationException("InstallationId must be configured to create an authenticated client.");

        var token = await auth.GetInstallationTokenAsync(installationId, cancellationToken);

        var client = new GitHubClient(new ProductHeaderValue("SpringVoyage"))
        {
            Credentials = new Credentials(token)
        };

        _logger.LogDebug("Created authenticated GitHub client for installation {InstallationId}", installationId);

        return client;
    }

    /// <summary>
    /// Gets all tool definitions provided by the GitHub connector.
    /// </summary>
    /// <returns>A read-only list of tool definitions.</returns>
    public IReadOnlyList<ToolDefinition> GetToolDefinitions() => skillRegistry.GetToolDefinitions();
}
