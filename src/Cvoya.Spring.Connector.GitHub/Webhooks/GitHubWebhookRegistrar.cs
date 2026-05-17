// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connector.GitHub.Webhooks;

using Cvoya.Spring.Connector.GitHub.Auth;

using Microsoft.Extensions.Logging;

using Octokit;

/// <summary>
/// Default <see cref="IGitHubWebhookRegistrar"/> implementation backed by
/// Octokit. Authenticates each call through the connector using the
/// per-unit installation id when supplied — issue #2385 made the binding-aware
/// path canonical so platform-side hook administration matches the
/// installation the unit was bound to. A <c>null</c> installation id falls
/// back to <see cref="GitHubConnector.CreateAuthenticatedClientAsync()"/>'s
/// global default; that fallback is reserved for OSS deployments that never
/// bound a per-unit installation and is the only remaining caller of the
/// global option.
/// </summary>
public class GitHubWebhookRegistrar(
    IGitHubConnector connector,
    GitHubConnectorOptions options,
    ILoggerFactory loggerFactory) : IGitHubWebhookRegistrar
{
    // Hard-coded fallback set, consulted only when the caller does not
    // supply an explicit Events list on the binding (issue #2423). Any unit
    // bound through PutConfigAsync with an explicit list overrides this
    // wholesale.
    private static readonly IReadOnlyList<string> DefaultSubscribedEvents = new[]
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
        long? installationId,
        IReadOnlyList<string>? events,
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

        var client = await CreateClientAsync(installationId, cancellationToken);

        var config = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["url"] = options.WebhookUrl,
            ["content_type"] = "json",
            ["secret"] = options.WebhookSecret,
            ["insecure_ssl"] = "0",
        };

        // Issue #2423: honour the per-unit Events list when the caller
        // supplied one; otherwise fall back to the hard-coded default so
        // legacy bindings (Events == null in the persisted config) keep
        // working unchanged. Empty is treated the same as null — both mean
        // "use the default set" per the issue's acceptance criteria.
        var subscribedEvents = events is { Count: > 0 }
            ? events
            : DefaultSubscribedEvents;

        var newHook = new NewRepositoryHook("web", config)
        {
            Events = subscribedEvents,
            Active = true,
        };

        _logger.LogInformation(
            "Creating GitHub webhook for {Owner}/{Repo} -> {Url} (installation {InstallationId}, events {Events})",
            owner, repo, options.WebhookUrl,
            installationId?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "<global>",
            string.Join(",", subscribedEvents));

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
        long? installationId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        ArgumentException.ThrowIfNullOrWhiteSpace(repo);

        var client = await CreateClientAsync(installationId, cancellationToken);

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

    /// <summary>
    /// Mints an Octokit client scoped to <paramref name="installationId"/>
    /// when supplied, otherwise falls back to the connector's global default.
    /// Centralised so both register / unregister apply the same auth rule
    /// (issue #2385).
    /// </summary>
    private Task<IGitHubClient> CreateClientAsync(
        long? installationId,
        CancellationToken cancellationToken)
    {
        return installationId is { } id and > 0
            ? connector.CreateAuthenticatedClientAsync(id, cancellationToken)
            : connector.CreateAuthenticatedClientAsync(cancellationToken);
    }
}
