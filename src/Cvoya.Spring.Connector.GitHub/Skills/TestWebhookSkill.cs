// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connector.GitHub.Skills;

using System.Text.Json;

using Microsoft.Extensions.Logging;

using Octokit;

/// <summary>
/// Triggers a synthetic <c>push</c> event for the given webhook. GitHub
/// exposes this as <c>POST /repos/:owner/:repo/hooks/:hook_id/tests</c> — the
/// <c>/tests</c> endpoint (not <c>/pings</c>) — which is what Octokit's
/// <c>Test</c> method calls. The endpoint returns 204 No Content, so a success
/// result has no further payload.
/// </summary>
public class TestWebhookSkill(IGitHubClient gitHubClient, ILoggerFactory loggerFactory)
{
    private readonly ILogger _logger = loggerFactory.CreateLogger<TestWebhookSkill>();

    /// <summary>
    /// Asks GitHub to redeliver the most recent <c>push</c> event to the hook.
    /// Useful for validating new hook configs end-to-end.
    /// </summary>
    public async Task<JsonElement> ExecuteAsync(
        string owner,
        string repo,
        long hookId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        ArgumentException.ThrowIfNullOrWhiteSpace(repo);

        var hookIdInt = checked((int)hookId);

        _logger.LogInformation(
            "Testing webhook {HookId} on {Owner}/{Repo}",
            hookId, owner, repo);

        await gitHubClient.Repository.Hooks.Test(owner, repo, hookIdInt);

        return JsonSerializer.SerializeToElement(new
        {
            tested = true,
            hook_id = hookId,
        });
    }
}
