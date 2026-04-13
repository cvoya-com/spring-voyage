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
public record UnitGitHubConfig(string Owner, string Repo);