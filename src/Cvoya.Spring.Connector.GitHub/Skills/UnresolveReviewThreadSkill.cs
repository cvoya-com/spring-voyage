// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connector.GitHub.Skills;

using System.Text.Json;

using Cvoya.Spring.Connector.GitHub.GraphQL;

using Microsoft.Extensions.Logging;

/// <summary>
/// Un-resolves a previously resolved review thread via the GraphQL
/// <c>unresolveReviewThread</c> mutation. Symmetric with
/// <see cref="ResolveReviewThreadSkill"/>; idempotent on an already
/// unresolved thread.
/// </summary>
public class UnresolveReviewThreadSkill(IGitHubGraphQLClient graphQLClient, ILoggerFactory loggerFactory)
{
    private readonly ILogger _logger = loggerFactory.CreateLogger<UnresolveReviewThreadSkill>();

    private const string Mutation = """
        mutation UnresolveReviewThread($threadId: ID!) {
          unresolveReviewThread(input: { threadId: $threadId }) {
            thread { id isResolved }
          }
        }
        """;

    /// <summary>Unresolves the given review thread.</summary>
    public async Task<JsonElement> ExecuteAsync(
        string threadId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(threadId))
        {
            throw new ArgumentException("threadId must be a non-empty GraphQL node id.", nameof(threadId));
        }

        _logger.LogInformation("Unresolving review thread {ThreadId}", threadId);

        UnresolveReviewThreadResponse response;
        try
        {
            response = await graphQLClient.MutateAsync<UnresolveReviewThreadResponse>(
                Mutation,
                new Dictionary<string, object?> { ["threadId"] = threadId },
                cancellationToken);
        }
        catch (GitHubGraphQLException ex) when (IsAlreadyUnresolvedError(ex))
        {
            _logger.LogInformation(
                "Review thread {ThreadId} already unresolved; treating unresolveReviewThread as a no-op.",
                threadId);
            return JsonSerializer.SerializeToElement(new
            {
                thread_id = threadId,
                is_resolved = false,
                no_op = true,
            });
        }

        var thread = response.UnresolveReviewThread?.Thread
            ?? throw new InvalidOperationException(
                $"unresolveReviewThread returned no thread payload for {threadId}.");

        return JsonSerializer.SerializeToElement(new
        {
            thread_id = thread.Id,
            is_resolved = thread.IsResolved,
            no_op = false,
        });
    }

    private static bool IsAlreadyUnresolvedError(GitHubGraphQLException ex)
    {
        foreach (var msg in ex.Errors)
        {
            if (msg.Contains("not resolved", StringComparison.OrdinalIgnoreCase)
                || msg.Contains("already unresolved", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }
}