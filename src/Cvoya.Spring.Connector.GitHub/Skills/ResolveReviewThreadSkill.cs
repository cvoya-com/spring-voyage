// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connector.GitHub.Skills;

using System.Text.Json;

using Cvoya.Spring.Connector.GitHub.GraphQL;

using Microsoft.Extensions.Logging;

/// <summary>
/// Resolves a review thread on a pull request via the GraphQL
/// <c>resolveReviewThread</c> mutation. Idempotent: calling on an already
/// resolved thread returns the same success shape with <c>no_op=true</c>
/// so agents can retry safely.
/// </summary>
public class ResolveReviewThreadSkill(IGitHubGraphQLClient graphQLClient, ILoggerFactory loggerFactory)
{
    private readonly ILogger _logger = loggerFactory.CreateLogger<ResolveReviewThreadSkill>();

    private const string Mutation = """
        mutation ResolveReviewThread($threadId: ID!) {
          resolveReviewThread(input: { threadId: $threadId }) {
            thread { id isResolved }
          }
        }
        """;

    /// <summary>
    /// Resolves the given review thread.
    /// </summary>
    /// <param name="threadId">The GraphQL node ID of the review thread.</param>
    public async Task<JsonElement> ExecuteAsync(
        string threadId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(threadId))
        {
            throw new ArgumentException("threadId must be a non-empty GraphQL node id.", nameof(threadId));
        }

        _logger.LogInformation("Resolving review thread {ThreadId}", threadId);

        ResolveReviewThreadResponse response;
        try
        {
            response = await graphQLClient.MutateAsync<ResolveReviewThreadResponse>(
                Mutation,
                new Dictionary<string, object?> { ["threadId"] = threadId },
                cancellationToken);
        }
        catch (GitHubGraphQLException ex) when (IsAlreadyResolvedError(ex))
        {
            // GitHub is inconsistent about treating "already resolved" as an
            // error vs. a successful no-op. Either way, the thread is in the
            // desired state — surface a no-op signal instead of bubbling the
            // error up to the caller.
            _logger.LogInformation(
                "Review thread {ThreadId} already resolved; treating resolveReviewThread as a no-op.",
                threadId);
            return JsonSerializer.SerializeToElement(new
            {
                thread_id = threadId,
                is_resolved = true,
                no_op = true,
            });
        }

        var thread = response.ResolveReviewThread?.Thread
            ?? throw new InvalidOperationException(
                $"resolveReviewThread returned no thread payload for {threadId}.");

        return JsonSerializer.SerializeToElement(new
        {
            thread_id = thread.Id,
            is_resolved = thread.IsResolved,
            no_op = false,
        });
    }

    private static bool IsAlreadyResolvedError(GitHubGraphQLException ex)
    {
        foreach (var msg in ex.Errors)
        {
            if (msg.Contains("already resolved", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }
}