// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Api.Models;

/// <summary>
/// Response item for <c>GET /api/v1/integrations/github/installations</c>.
/// Thin projection of the GitHub App installations visible to the configured
/// App — identical to
/// <see cref="Cvoya.Spring.Connector.GitHub.Auth.GitHubInstallation"/> but
/// duplicated here so Models has no connector-specific transport type in its
/// OpenAPI contract.
/// </summary>
/// <param name="InstallationId">The numeric installation id.</param>
/// <param name="Account">The account login the App is installed on.</param>
/// <param name="AccountType">Either <c>User</c> or <c>Organization</c>.</param>
/// <param name="RepoSelection">Either <c>all</c> or <c>selected</c>.</param>
public record GitHubInstallationResponse(
    long InstallationId,
    string Account,
    string AccountType,
    string RepoSelection);

/// <summary>
/// Response body for <c>GET /api/v1/integrations/github/install-url</c>.
/// Separate response type (as opposed to plain <c>{ url }</c>) so the
/// OpenAPI contract stays stable when extra fields are added later — e.g.
/// a <c>state</c> token that survives the round trip.
/// </summary>
/// <param name="Url">The install URL the user should be sent to.</param>
public record GitHubInstallUrlResponse(string Url);