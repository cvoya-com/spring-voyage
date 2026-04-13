// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Units;

/// <summary>
/// Per-unit GitHub connector configuration. Persisted on the unit actor so the
/// platform can register a webhook on the configured repository when the unit
/// starts and delete it when the unit stops. Separate from
/// <see cref="UnitMetadata"/> because this is an integration binding rather
/// than display metadata, and future connectors (Slack, Linear, etc.) will
/// carry their own typed config records.
/// </summary>
/// <param name="Owner">The repository owner (user or organization login).</param>
/// <param name="Repo">The repository name.</param>
/// <param name="AppInstallationId">
/// The GitHub App installation id selected by the user when binding this unit
/// to a repository. <c>null</c> when the unit was configured before the
/// installation-selection flow was introduced, in which case the connector
/// falls back to <c>GitHubConnectorOptions.InstallationId</c>.
/// </param>
/// <param name="Events">
/// The webhook event names the unit subscribes to (e.g. <c>issues</c>,
/// <c>pull_request</c>, <c>issue_comment</c>). <c>null</c> falls back to the
/// connector's default set so legacy config keeps working.
/// </param>
public record UnitGitHubConfig(
    string Owner,
    string Repo,
    long? AppInstallationId = null,
    IReadOnlyList<string>? Events = null);