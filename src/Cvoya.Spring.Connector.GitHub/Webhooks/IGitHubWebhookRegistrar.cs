// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connector.GitHub.Webhooks;

/// <summary>
/// Registers and unregisters GitHub repository webhooks on behalf of the
/// platform. Extracted into an interface so the unit lifecycle handler can
/// depend on a minimal, mockable surface rather than the full
/// <see cref="GitHubConnector"/> — mirrors the split introduced for skill
/// registries.
/// </summary>
public interface IGitHubWebhookRegistrar
{
    /// <summary>
    /// Creates a repository webhook that forwards <c>issues</c>,
    /// <c>pull_request</c>, and <c>issue_comment</c> events to the platform's
    /// configured webhook URL, signed with the shared secret.
    /// </summary>
    /// <param name="owner">The repository owner.</param>
    /// <param name="repo">The repository name.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The id of the newly created hook.</returns>
    Task<long> RegisterAsync(string owner, string repo, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes the specified repository webhook. Implementations treat a 404
    /// response from GitHub as success (the hook was already gone) so that a
    /// stale handle does not block unit teardown.
    /// </summary>
    /// <param name="owner">The repository owner.</param>
    /// <param name="repo">The repository name.</param>
    /// <param name="hookId">The hook id returned by <see cref="RegisterAsync"/>.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    Task UnregisterAsync(string owner, string repo, long hookId, CancellationToken cancellationToken = default);
}