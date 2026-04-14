// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connector.GitHub.Webhooks;

using Cvoya.Spring.Connector.GitHub.Auth;

using Microsoft.Extensions.Logging;

using Octokit;

/// <summary>
/// Default <see cref="IGitHubWebhookRegistrar"/> implementation backed by
/// Octokit. Authenticates each call through
/// <see cref="GitHubConnector.CreateAuthenticatedClientAsync"/> so the same
/// GitHub App installation token drives both tool calls and hook
/// administration.
/// </summary>
public class GitHubWebhookRegistrar(
    GitHubConnector connector,
    GitHubConnectorOptions options,
    ILoggerFactory loggerFactory) : IGitHubWebhookRegistrar
{
    private static readonly IReadOnlyList<string> SubscribedEvents = new[]
    {
        "issues",
        "pull_request",
        "issue_comment",
        "pull_request_review",
        "pull_request_review_comment",
        "pull_request_review_thread",
    };

    private readonly ILogger _logger = loggerFactory.CreateLogger<GitHubWebhookRegistrar>();

    /// <inheritdoc />
    public async Task<long> RegisterAsync(
        string owner,
        string repo,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        ArgumentException.ThrowIfNullOrWhiteSpace(repo);

        if (string.IsNullOrWhiteSpace(options.WebhookUrl))
        {
            throw new InvalidOperationException(
                "GitHub:WebhookUrl must be configured before registering repository webhooks.");
        }

        if (string.IsNullOrEmpty(options.WebhookSecret))
        {
            throw new InvalidOperationException(
                "GitHub:WebhookSecret must be configured before registering repository webhooks.");
        }

        var client = await connector.CreateAuthenticatedClientAsync(cancellationToken);

        var config = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["url"] = options.WebhookUrl,
            ["content_type"] = "json",
            ["secret"] = options.WebhookSecret,
            ["insecure_ssl"] = "0",
        };

        var newHook = new NewRepositoryHook("web", config)
        {
            Events = SubscribedEvents,
            Active = true,
        };

        _logger.LogInformation(
            "Creating GitHub webhook for {Owner}/{Repo} -> {Url}",
            owner, repo, options.WebhookUrl);

        var created = await client.Repository.Hooks.Create(owner, repo, newHook);

        _logger.LogInformation(
            "Created GitHub webhook {HookId} for {Owner}/{Repo}",
            created.Id, owner, repo);

        return created.Id;
    }

    /// <inheritdoc />
    public async Task UnregisterAsync(
        string owner,
        string repo,
        long hookId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        ArgumentException.ThrowIfNullOrWhiteSpace(repo);

        var client = await connector.CreateAuthenticatedClientAsync(cancellationToken);

        try
        {
            // Octokit's Delete takes Int32; hook ids returned from the API can
            // exceed that range. Cast with an overflow check so any future
            // failure surfaces clearly rather than silently wrapping.
            var hookIdInt = checked((int)hookId);
            await client.Repository.Hooks.Delete(owner, repo, hookIdInt);
            _logger.LogInformation(
                "Deleted GitHub webhook {HookId} on {Owner}/{Repo}",
                hookId, owner, repo);
        }
        catch (NotFoundException)
        {
            // Hook is already gone (admin deleted it, repo was recreated, etc.).
            // Per #114 the /stop handler must log-but-not-fail in this case so
            // teardown can proceed to Stopped.
            _logger.LogWarning(
                "GitHub webhook {HookId} not found on {Owner}/{Repo}; assuming already deleted",
                hookId, owner, repo);
        }
    }
}