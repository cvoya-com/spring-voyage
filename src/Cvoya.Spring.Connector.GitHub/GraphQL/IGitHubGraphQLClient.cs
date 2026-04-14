// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connector.GitHub.GraphQL;

/// <summary>
/// Minimal GraphQL client abstraction layered on top of Octokit's
/// <see cref="Octokit.IConnection"/>. Hand-rolled DTOs per query keep the
/// dependency surface small — we intentionally avoid <c>Octokit.GraphQL</c>
/// until there's a compelling reason to take on a schema-first codegen
/// dependency.
/// </summary>
/// <remarks>
/// Both <see cref="QueryAsync{T}"/> and <see cref="MutateAsync{T}"/> submit
/// the same HTTP POST shape (<c>{ query, variables }</c> to <c>/graphql</c>);
/// they're separate methods only to keep caller intent explicit in the
/// skill code. Implementations must surface the GraphQL <c>errors</c> array
/// as a <see cref="GitHubGraphQLException"/> and return <typeparamref name="T"/>
/// on success.
/// </remarks>
public interface IGitHubGraphQLClient
{
    /// <summary>
    /// Executes a GraphQL query and returns the deserialized <c>data</c>
    /// payload as <typeparamref name="T"/>.
    /// </summary>
    /// <typeparam name="T">The response DTO shape.</typeparam>
    /// <param name="query">The GraphQL query string.</param>
    /// <param name="variables">An object whose public properties become the
    /// <c>variables</c> dictionary. <c>null</c> is allowed when the query
    /// takes no variables.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    Task<T> QueryAsync<T>(string query, object? variables, CancellationToken cancellationToken = default);

    /// <summary>
    /// Executes a GraphQL mutation. Identical transport semantics to
    /// <see cref="QueryAsync{T}"/>; kept separate so skill call sites read
    /// clearly.
    /// </summary>
    Task<T> MutateAsync<T>(string mutation, object? variables, CancellationToken cancellationToken = default);
}