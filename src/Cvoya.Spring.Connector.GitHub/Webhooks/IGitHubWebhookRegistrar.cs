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
    /// Creates a repository webhook that forwards events to the platform's
    /// configured webhook URL, signed with the shared secret. The hook is
    /// created in the scope of <paramref name="installationId"/> when
    /// supplied — issue #2385 made this the canonical path so platform-side
    /// webhook administration matches the binding the unit was configured
    /// with. <c>null</c> falls back to the connector's global default
    /// installation id and is reserved for OSS deployments that never bound a
    /// per-unit installation; the global option is documented as a fallback
    /// only.
    ///
    /// <para>
    /// <paramref name="events"/> drives the event list the hook subscribes
    /// to. When <c>null</c> or empty the implementation falls back to its
    /// hard-coded default list — that fallback exists for legacy bindings
    /// that never persisted an explicit <see cref="UnitGitHubConfig.Events"/>
    /// list and is the only remaining caller of the hard-coded constant
    /// (issue #2423).
    /// </para>
    /// </summary>
    /// <param name="owner">The repository owner.</param>
    /// <param name="repo">The repository name.</param>
    /// <param name="installationId">
    /// The GitHub App installation id from the unit binding's
    /// <see cref="UnitGitHubConfig.AppInstallationId"/>, or <c>null</c> to use
    /// the connector's global default installation id.
    /// </param>
    /// <param name="events">
    /// The webhook event names the hook should subscribe to (e.g.
    /// <c>issues</c>, <c>pull_request</c>). <c>null</c> or empty falls back
    /// to the connector's hard-coded default set (issue #2423).
    /// </param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The id of the newly created hook.</returns>
    Task<long> RegisterAsync(
        string owner,
        string repo,
        long? installationId,
        IReadOnlyList<string>? events,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes the specified repository webhook. Implementations treat a 404
    /// response from GitHub as success (the hook was already gone) so that a
    /// stale handle does not block unit teardown. The delete call uses the
    /// supplied <paramref name="installationId"/> — pass the binding's
    /// installation id so teardown authenticates against the same scope that
    /// created the hook. Issue #2385.
    /// </summary>
    /// <param name="owner">The repository owner.</param>
    /// <param name="repo">The repository name.</param>
    /// <param name="hookId">The hook id returned by <see cref="RegisterAsync"/>.</param>
    /// <param name="installationId">
    /// The GitHub App installation id from the unit binding's
    /// <see cref="UnitGitHubConfig.AppInstallationId"/>, or <c>null</c> to use
    /// the connector's global default installation id.
    /// </param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    Task UnregisterAsync(
        string owner,
        string repo,
        long hookId,
        long? installationId,
        CancellationToken cancellationToken = default);
}
