// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connector.GitHub.Skills;

using System.Net;
using System.Text.Json;

using Microsoft.Extensions.Logging;

using Octokit;

/// <summary>
/// Updates a pull request branch by merging the base branch into it. Equivalent to
/// the "Update branch" button in the GitHub UI; backed by
/// <c>PUT /repos/{owner}/{repo}/pulls/{pull_number}/update-branch</c>.
/// Octokit doesn't have a typed binding for this endpoint, so we go through the
/// raw <see cref="IConnection"/>. A 422 response indicates the branch is either
/// already up to date or a merge conflict blocks the update — we surface that as
/// a clean <c>updated=false</c> result with the reason in the payload.
/// </summary>
public class UpdateBranchSkill(IGitHubClient gitHubClient, ILoggerFactory loggerFactory)
{
    private readonly ILogger _logger = loggerFactory.CreateLogger<UpdateBranchSkill>();

    /// <summary>
    /// Updates the PR branch with the latest from the base branch.
    /// </summary>
    /// <param name="owner">The repository owner.</param>
    /// <param name="repo">The repository name.</param>
    /// <param name="number">The pull request number.</param>
    /// <param name="expectedHeadSha">
    /// Optional expected head SHA. If supplied and the PR head has moved, GitHub
    /// returns 422 and this skill reports <c>updated=false</c>.
    /// </param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The result as a JSON element describing the update outcome.</returns>
    public async Task<JsonElement> ExecuteAsync(
        string owner,
        string repo,
        int number,
        string? expectedHeadSha,
        CancellationToken cancellationToken = default)
    {
        var uri = new Uri($"repos/{owner}/{repo}/pulls/{number}/update-branch", UriKind.Relative);
        var body = string.IsNullOrWhiteSpace(expectedHeadSha)
            ? new Dictionary<string, string>()
            : new Dictionary<string, string> { ["expected_head_sha"] = expectedHeadSha };

        _logger.LogInformation(
            "Updating branch for {Owner}/{Repo}#{Number} expectedHead={Sha}",
            owner, repo, number, expectedHeadSha ?? "*");

        try
        {
            var response = await gitHubClient.Connection.Put<UpdateBranchResponse>(uri, body);
            return JsonSerializer.SerializeToElement(new
            {
                updated = true,
                status_code = (int)response.HttpResponse.StatusCode,
                message = response.Body?.Message,
                url = response.Body?.Url,
            });
        }
        catch (ApiValidationException ex)
        {
            // 422: the branch is already up to date, or there's a merge conflict.
            _logger.LogInformation(
                "Update-branch returned validation failure for {Owner}/{Repo}#{Number}: {Message}",
                owner, repo, number, ex.Message);
            return JsonSerializer.SerializeToElement(new
            {
                updated = false,
                status_code = (int)HttpStatusCode.UnprocessableEntity,
                message = ex.Message,
            });
        }
    }

    /// <summary>
    /// Response shape for the update-branch REST endpoint.
    /// </summary>
    public sealed class UpdateBranchResponse
    {
        /// <summary>The message returned by GitHub.</summary>
        public string? Message { get; set; }

        /// <summary>The URL returned by GitHub for polling status.</summary>
        public string? Url { get; set; }
    }
}
