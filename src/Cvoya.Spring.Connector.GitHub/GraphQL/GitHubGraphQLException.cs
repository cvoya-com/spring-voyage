// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connector.GitHub.GraphQL;

/// <summary>
/// Raised when GitHub returns a non-empty <c>errors</c> array in a GraphQL
/// response. Carries the individual error messages so skills can surface
/// them cleanly instead of throwing a generic <see cref="InvalidOperationException"/>.
/// </summary>
public class GitHubGraphQLException : InvalidOperationException
{
    /// <summary>
    /// Initializes a new instance with the joined error message text.
    /// </summary>
    public GitHubGraphQLException(IReadOnlyList<string> errors)
        : base("GitHub GraphQL request failed: " + string.Join("; ", errors))
    {
        Errors = errors;
    }

    /// <summary>
    /// The per-error messages returned by GitHub.
    /// </summary>
    public IReadOnlyList<string> Errors { get; }
}