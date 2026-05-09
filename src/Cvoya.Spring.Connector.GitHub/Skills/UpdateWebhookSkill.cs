// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connector.GitHub.Skills;

using System.Text.Json;

using Microsoft.Extensions.Logging;

using Octokit;

/// <summary>
/// Updates an existing repository webhook: replaces the events list, toggles
/// active, and/or patches config (url, content_type, secret, insecure_ssl).
/// Unspecified inputs are left untouched. Mirrors GitHub's
/// <c>PATCH /repos/:owner/:repo/hooks/:hook_id</c>.
/// </summary>
public class UpdateWebhookSkill(IGitHubClient gitHubClient, ILoggerFactory loggerFactory)
{
    private readonly ILogger _logger = loggerFactory.CreateLogger<UpdateWebhookSkill>();

    /// <summary>
    /// Updates the specified webhook. Any parameter left null is omitted from
    /// the PATCH so the hook's existing value is preserved.
    /// </summary>
    public async Task<JsonElement> ExecuteAsync(
        string owner,
        string repo,
        long hookId,
        string[]? events,
        bool? active,
        string? url,
        string? contentType,
        string? secret,
        bool? insecureSsl,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        ArgumentException.ThrowIfNullOrWhiteSpace(repo);

        // Octokit's Edit takes Int32; hook ids returned from the API can
        // exceed that range. Cast with an overflow check so any future
        // failure surfaces clearly rather than silently wrapping.
        var hookIdInt = checked((int)hookId);

        _logger.LogInformation(
            "Updating webhook {HookId} on {Owner}/{Repo}",
            hookId, owner, repo);

        var edit = new EditRepositoryHook(new Dictionary<string, string>(StringComparer.Ordinal));

        if (events is { Length: > 0 })
        {
            edit.Events = events;
        }

        if (active.HasValue)
        {
            edit.Active = active.Value;
        }

        if (!string.IsNullOrWhiteSpace(url))
        {
            edit.Config["url"] = url;
        }

        if (!string.IsNullOrWhiteSpace(contentType))
        {
            edit.Config["content_type"] = contentType;
        }

        if (!string.IsNullOrWhiteSpace(secret))
        {
            edit.Config["secret"] = secret;
        }

        if (insecureSsl.HasValue)
        {
            edit.Config["insecure_ssl"] = insecureSsl.Value ? "1" : "0";
        }

        var updated = await gitHubClient.Repository.Hooks.Edit(owner, repo, hookIdInt, edit);

        return JsonSerializer.SerializeToElement(WebhookProjection.Project(updated));
    }
}
