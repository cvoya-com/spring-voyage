// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connector.GitHub.Webhooks;

/// <summary>
/// Lazy fetcher for the changed-files list backing a pull request, used to
/// hydrate the <c>files</c> array on translated PR-shape webhook payloads
/// when (and only when) the unit binding actually configures
/// <see cref="UnitGitHubConfig.IncludePaths"/>. Extracted so the dispatcher
/// can avoid an extra <c>GET /repos/{owner}/{repo}/pulls/{number}/files</c>
/// call for every PR webhook — the round-trip is gated on the filter shape
/// rather than paid unconditionally at translation time. Issue #2407.
/// </summary>
/// <remarks>
/// Behaviour contract:
/// <list type="bullet">
///   <item>Returns the full list of changed file paths (across all pages) on success.</item>
///   <item>Returns <c>null</c> on transport failure / rate-limit / 4xx
///   (other than 404). Callers MUST treat null as "fail-open" — pass the
///   event through unfiltered — consistent with the binding-load failure
///   pattern already in <see cref="GitHubWebhookHandler.ApplyInboundFilterAsync"/>.</item>
///   <item>Returns an empty list on 404 (PR no longer exists / was hard-
///   deleted between the webhook firing and our fetch).</item>
///   <item>Caps the fetch at an implementation-defined upper bound to
///   protect against degenerate PRs touching thousands of files. When the
///   cap is exceeded, returns an empty list so the path filter fails closed
///   (the operator's <c>IncludePaths</c> can't be honoured reliably on a
///   capped list, and skipping the drop would mis-route a PR that may not
///   match the configured path prefixes).</item>
/// </list>
/// </remarks>
public interface IGitHubPullRequestFilesFetcher
{
    /// <summary>
    /// Fetches the changed-files list for the specified pull request,
    /// authenticated through the binding's pinned credential per
    /// ADR-0047 §6 (App-installation token mint OR tenant-secret-store
    /// PAT, whichever the binding chose at create time).
    /// </summary>
    /// <param name="owner">The repository owner login.</param>
    /// <param name="repo">The repository name.</param>
    /// <param name="number">The pull request number.</param>
    /// <param name="binding">
    /// The unit's GitHub binding payload. Drives outbound auth through
    /// <see cref="Auth.GitHubBindingAuthResolver"/>: App-installation
    /// token mint when <see cref="UnitGitHubConfig.AppInstallationId"/>
    /// is set, tenant-secret-store PAT read when
    /// <see cref="UnitGitHubConfig.PatSecretName"/> is set.
    /// </param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>
    /// The changed file paths, or <c>null</c> when the fetch failed (caller
    /// must fail open). Empty list when the PR is gone or exceeds the
    /// implementation cap (caller treats as "no path matched").
    /// </returns>
    Task<IReadOnlyList<string>?> FetchAsync(
        string owner,
        string repo,
        int number,
        UnitGitHubConfig binding,
        CancellationToken cancellationToken = default);
}
