// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connector.GitHub.GraphQL;

using System.Text.Json.Serialization;

/// <summary>
/// Top-level envelope for <c>{ repository { pullRequest { reviewThreads } } }</c>
/// queries. The GraphQL field names are lower-camelCase, which we map via
/// <see cref="JsonPropertyNameAttribute"/>.
/// </summary>
public sealed record ReviewThreadsResponse(
    [property: JsonPropertyName("repository")] RepositoryWithPullRequest? Repository);

/// <summary>A repository containing a nested pull request.</summary>
public sealed record RepositoryWithPullRequest(
    [property: JsonPropertyName("pullRequest")] PullRequestWithReviewThreads? PullRequest);

/// <summary>A pull request with its paged <c>reviewThreads</c> connection.</summary>
public sealed record PullRequestWithReviewThreads(
    [property: JsonPropertyName("reviewThreads")] ReviewThreadConnection ReviewThreads);

/// <summary>Minimal <c>reviewThreads</c> connection — nodes only; callers
/// page externally if they need more than the first N.</summary>
public sealed record ReviewThreadConnection(
    [property: JsonPropertyName("nodes")] IReadOnlyList<ReviewThreadNode> Nodes);

/// <summary>
/// A single review thread — the GraphQL-only state GitHub exposes for PR
/// conversations. The REST API returns review *comments* but not the
/// thread-level resolution state, which is what this DTO captures.
/// </summary>
public sealed record ReviewThreadNode(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("isResolved")] bool IsResolved,
    [property: JsonPropertyName("isOutdated")] bool IsOutdated,
    [property: JsonPropertyName("path")] string? Path,
    [property: JsonPropertyName("line")] int? Line,
    [property: JsonPropertyName("comments")] ReviewThreadCommentConnection Comments);

/// <summary>Thread-scoped comments connection (first N nodes).</summary>
public sealed record ReviewThreadCommentConnection(
    [property: JsonPropertyName("nodes")] IReadOnlyList<ReviewThreadComment> Nodes);

/// <summary>A single review comment inside a thread.</summary>
public sealed record ReviewThreadComment(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("databaseId")] long? DatabaseId,
    [property: JsonPropertyName("body")] string Body,
    [property: JsonPropertyName("author")] ReviewThreadAuthor? Author);

/// <summary>Author of a review comment.</summary>
public sealed record ReviewThreadAuthor(
    [property: JsonPropertyName("login")] string Login);

/// <summary>Envelope for the <c>resolveReviewThread</c> mutation response.</summary>
public sealed record ResolveReviewThreadResponse(
    [property: JsonPropertyName("resolveReviewThread")] ResolveReviewThreadPayload? ResolveReviewThread);

/// <summary>Envelope for the <c>unresolveReviewThread</c> mutation response.</summary>
public sealed record UnresolveReviewThreadResponse(
    [property: JsonPropertyName("unresolveReviewThread")] ResolveReviewThreadPayload? UnresolveReviewThread);

/// <summary>Payload shared by resolve/unresolve mutations — both return
/// the mutated <c>thread</c>.</summary>
public sealed record ResolveReviewThreadPayload(
    [property: JsonPropertyName("thread")] ReviewThreadState? Thread);

/// <summary>Bare resolution state returned by the mutations.</summary>
public sealed record ReviewThreadState(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("isResolved")] bool IsResolved);