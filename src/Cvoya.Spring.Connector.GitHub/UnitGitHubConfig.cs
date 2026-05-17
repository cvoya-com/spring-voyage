// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connector.GitHub;

using System.Text.Json.Serialization;

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
/// unit to a repository. Drives every platform-side outbound auth call for
/// the binding — webhook register / unregister, label roundtrip, the
/// per-binding PR-files fetcher (#2385). <c>null</c> falls back to
/// <see cref="Auth.GitHubConnectorOptions.InstallationId"/>; that fallback
/// is the documented OSS path for single-installation deployments and
/// should not be relied on by any deployment with more than one
/// installation visible to the App.
/// </param>
/// <param name="Events">
/// The webhook event names the unit subscribes to (e.g. <c>issues</c>,
/// <c>pull_request</c>, <c>issue_comment</c>). <c>null</c> falls back to
/// the connector's default set so legacy config keeps working.
/// </param>
/// <param name="Reviewer">
/// Default GitHub login (without the leading <c>@</c>) requested as the
/// reviewer on pull requests this unit opens. <c>null</c> means "no
/// default reviewer" — agents that explicitly pass a reviewer to
/// <c>gh pr edit --add-reviewer</c> (or the equivalent <c>gh</c> /
/// <c>git</c> invocation) still override per-call. Per #2384 / #2383
/// the platform no longer surfaces a <c>github.*</c> review-request
/// MCP tool; agents reach GitHub through the in-container CLIs.
/// </param>
/// <param name="AddOnAssign">Labels to add when an issue is assigned through this unit.</param>
/// <param name="RemoveOnAssign">Labels to remove when an issue is assigned through this unit.</param>
/// <param name="IncludeLabels">
/// Inbound webhook filter: when non-empty, the unit only receives events
/// whose subject (issue or PR) carries at least one of these labels. Null
/// or empty means "no label include filter." Issue #2407.
/// </param>
/// <param name="ExcludeLabels">
/// Inbound webhook filter: when non-empty, drops events whose subject
/// carries any of these labels — evaluated first, short-circuits the
/// other filter kinds. Issue #2407.
/// </param>
/// <param name="IncludeAuthors">
/// Inbound webhook filter: when non-empty, the unit only receives events
/// authored by one of these GitHub logins (issue author, PR author, or
/// the comment author for comment events). Null or empty means "no
/// author filter." Issue #2407.
/// </param>
/// <param name="IncludePaths">
/// Inbound webhook filter: when non-empty, the unit only receives PR-shape
/// events whose changed file set intersects one of these paths. Pure
/// issue events ignore this filter (they have no changed files). Path
/// matching is prefix-based — <c>docs/</c> matches <c>docs/foo.md</c>.
/// Null or empty means "no path filter." Issue #2407.
/// </param>
public record UnitGitHubConfig(
    string Owner,
    string Repo,
    long? AppInstallationId = null,
    IReadOnlyList<string>? Events = null,
    string? Reviewer = null,
    [property: JsonPropertyName("add_on_assign")] IReadOnlyList<string>? AddOnAssign = null,
    [property: JsonPropertyName("remove_on_assign")] IReadOnlyList<string>? RemoveOnAssign = null,
    [property: JsonPropertyName("include_labels")] IReadOnlyList<string>? IncludeLabels = null,
    [property: JsonPropertyName("exclude_labels")] IReadOnlyList<string>? ExcludeLabels = null,
    [property: JsonPropertyName("include_authors")] IReadOnlyList<string>? IncludeAuthors = null,
    [property: JsonPropertyName("include_paths")] IReadOnlyList<string>? IncludePaths = null);
