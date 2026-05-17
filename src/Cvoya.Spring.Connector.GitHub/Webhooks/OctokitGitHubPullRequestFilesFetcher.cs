// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connector.GitHub.Webhooks;

using System.Net;

using Microsoft.Extensions.Logging;

using Octokit;

/// <summary>
/// Default <see cref="IGitHubPullRequestFilesFetcher"/> built on
/// <see cref="IGitHubConnector.CreateAuthenticatedClientAsync"/>. Authenticates
/// as the connector's configured GitHub App installation and pages through
/// <c>GET /repos/{owner}/{repo}/pulls/{number}/files</c> via Octokit's
/// <see cref="IPullRequestsClient.Files(string, string, int, ApiOptions)"/>.
/// </summary>
/// <remarks>
/// <para>
/// Pagination: GitHub's files endpoint defaults to 30 items per page and caps
/// at 100. We use the 100-per-page maximum so the smallest reasonable PRs
/// resolve in a single round-trip; large PRs roll up to <see cref="MaxPages"/>
/// pages (= <see cref="MaxFiles"/> files in total). Anything larger is
/// degenerate — code-style PRs touching every file in a tree — and the
/// fetcher fails the filter closed rather than truncating silently.
/// </para>
/// <para>
/// Caching is deliberately omitted from the OSS fetcher today; the fetch is
/// gated on <see cref="UnitGitHubConfig.IncludePaths"/> being configured, so
/// the per-event cost is paid only by bindings that opted in. A response
/// cache (matching the existing <c>IGitHubResponseCache</c> pattern) is
/// tracked as a follow-up — see GitHub issue #2415.
/// </para>
/// </remarks>
public class OctokitGitHubPullRequestFilesFetcher : IGitHubPullRequestFilesFetcher
{
    /// <summary>
    /// Maximum number of files per page passed to <see cref="ApiOptions"/>.
    /// GitHub caps this at 100 — anything higher is silently truncated.
    /// </summary>
    private const int PageSize = 100;

    /// <summary>
    /// Maximum number of pages we'll fetch. With <see cref="PageSize"/> of
    /// 100 this gives an effective ceiling of 3000 files per PR. PRs that
    /// exceed this are treated as "filter unable to evaluate" and the
    /// caller drops the event (see <see cref="IGitHubPullRequestFilesFetcher"/>
    /// remarks for the rationale).
    /// </summary>
    private const int MaxPages = 30;

    /// <summary>Maximum number of file paths the fetcher will surface.</summary>
    public const int MaxFiles = PageSize * MaxPages;

    private readonly Func<IGitHubConnector> _connectorAccessor;
    private readonly ILogger _logger;

    /// <summary>
    /// Initializes a new fetcher against a lazy connector accessor. The
    /// accessor breaks the otherwise-cyclic singleton graph
    /// (<see cref="GitHubWebhookHandler"/> → fetcher → connector →
    /// <see cref="GitHubWebhookHandler"/>) — the connector is resolved on
    /// first call rather than at construction.
    /// </summary>
    /// <param name="connectorAccessor">Lazy resolver for the GitHub connector.</param>
    /// <param name="loggerFactory">Logger factory.</param>
    public OctokitGitHubPullRequestFilesFetcher(
        Func<IGitHubConnector> connectorAccessor,
        ILoggerFactory loggerFactory)
    {
        ArgumentNullException.ThrowIfNull(connectorAccessor);
        ArgumentNullException.ThrowIfNull(loggerFactory);
        _connectorAccessor = connectorAccessor;
        _logger = loggerFactory.CreateLogger<OctokitGitHubPullRequestFilesFetcher>();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<string>?> FetchAsync(
        string owner,
        string repo,
        int number,
        long? installationId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        ArgumentException.ThrowIfNullOrWhiteSpace(repo);
        if (number <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(number));
        }

        // The OSS connector only carries a single configured installation id
        // (GitHubConnectorOptions.InstallationId), so the binding-level
        // override is currently informational — the cloud repo, which can
        // substitute its own IGitHubPullRequestFilesFetcher implementation,
        // is the place to honour per-binding installations. Log the override
        // so operators can spot the discrepancy in OSS logs.
        if (installationId.HasValue)
        {
            _logger.LogDebug(
                "Fetching changed files for {Owner}/{Repo}#{Number} with binding installation hint {InstallationId} "
                + "(OSS fetcher uses connector default).",
                owner, repo, number, installationId.Value);
        }

        IGitHubClient client;
        try
        {
            var connector = _connectorAccessor()
                ?? throw new InvalidOperationException("Connector accessor returned null.");
            client = await connector.CreateAuthenticatedClientAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Failed to create authenticated GitHub client for {Owner}/{Repo}#{Number}; path filter will fail open.",
                owner, repo, number);
            return null;
        }

        var collected = new List<string>(capacity: PageSize);

        for (var page = 1; page <= MaxPages; page++)
        {
            var options = new ApiOptions
            {
                PageSize = PageSize,
                PageCount = 1,
                StartPage = page,
            };

            IReadOnlyList<PullRequestFile> pageFiles;
            try
            {
                pageFiles = await client.PullRequest
                    .Files(owner, repo, number, options)
                    .ConfigureAwait(false);
            }
            catch (NotFoundException)
            {
                // PR was hard-deleted between the webhook firing and our
                // fetch (rare but possible with admin force-delete). Return
                // an empty list — the path filter then has no paths to
                // match and drops the event closed, which is the correct
                // behaviour for a vanished PR.
                _logger.LogInformation(
                    "Pull request {Owner}/{Repo}#{Number} not found while fetching files; treating as empty.",
                    owner, repo, number);
                return Array.Empty<string>();
            }
            catch (RateLimitExceededException ex)
            {
                _logger.LogWarning(ex,
                    "Rate limit exceeded fetching changed files for {Owner}/{Repo}#{Number}; path filter will fail open.",
                    owner, repo, number);
                return null;
            }
            catch (AbuseException ex)
            {
                _logger.LogWarning(ex,
                    "Abuse-limit hit fetching changed files for {Owner}/{Repo}#{Number}; path filter will fail open.",
                    owner, repo, number);
                return null;
            }
            catch (ApiException ex) when ((int)ex.StatusCode >= 500)
            {
                _logger.LogWarning(ex,
                    "GitHub returned {StatusCode} fetching changed files for {Owner}/{Repo}#{Number}; path filter will fail open.",
                    (int)ex.StatusCode, owner, repo, number);
                return null;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex,
                    "Unexpected error fetching changed files for {Owner}/{Repo}#{Number}; path filter will fail open.",
                    owner, repo, number);
                return null;
            }

            foreach (var f in pageFiles)
            {
                if (!string.IsNullOrEmpty(f.FileName))
                {
                    collected.Add(f.FileName);
                }
            }

            // The PR has fewer than PageSize files left — we've drained the
            // collection and can stop without burning another paginated call.
            if (pageFiles.Count < PageSize)
            {
                return collected;
            }

            // Final iteration hit the page-cap with a full page; the PR
            // touches >= MaxFiles paths. Fail closed: clear the buffer so
            // the caller's filter sees no matches and drops the event.
            // Logging at warning so operators see this and can file a real
            // issue (cap raise) rather than silently routing or silently
            // dropping mega-PRs.
            if (page == MaxPages)
            {
                _logger.LogWarning(
                    "Pull request {Owner}/{Repo}#{Number} touches at least {MaxFiles} files — exceeds the fetcher cap; "
                    + "treating as no-match for path filter (fail-closed). Raising the cap is tracked in issue #2416.",
                    owner, repo, number, MaxFiles);
                return Array.Empty<string>();
            }
        }

        // Unreachable — the loop either returns from the short page check
        // or hits the cap branch above. Defensive return to keep the
        // compiler quiet about all code paths.
        return collected;
    }
}
