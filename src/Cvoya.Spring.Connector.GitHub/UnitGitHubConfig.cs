// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connector.GitHub;

/// <summary>
/// Per-unit GitHub connector configuration. Persisted on the unit actor
/// through the generic <see cref="Connectors.IUnitConnectorConfigStore"/>
/// abstraction — serialized as a <see cref="System.Text.Json.JsonElement"/>
/// so the core platform remains unaware of any connector-specific shape.
/// </summary>
/// <param name="Owner">The repository owner (user or organization login).</param>
/// <param name="Repo">The repository name.</param>
/// <param name="AppInstallationId">
/// The GitHub App installation id selected by the user when binding this
/// unit to a repository. <c>null</c> falls back to
/// <see cref="Auth.GitHubConnectorOptions.InstallationId"/>.
/// </param>
/// <param name="Events">
/// The webhook event names the unit subscribes to (e.g. <c>issues</c>,
/// <c>pull_request</c>, <c>issue_comment</c>). <c>null</c> falls back to
/// the connector's default set so legacy config keeps working.
/// </param>
public record UnitGitHubConfig(
    string Owner,
    string Repo,
    long? AppInstallationId = null,
    IReadOnlyList<string>? Events = null);