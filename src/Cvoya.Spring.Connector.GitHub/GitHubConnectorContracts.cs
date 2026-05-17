// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connector.GitHub;

using System.Text.Json.Serialization;

/// <summary>
/// Request body for
/// <c>PUT /api/v1/connectors/github/units/{unitId}/config</c>. Binds the
/// unit to the GitHub connector and upserts the per-unit config atomically.
/// </summary>
/// <param name="Owner">The repository owner (user or organization login).</param>
/// <param name="Repo">The repository name.</param>
/// <param name="AppInstallationId">The GitHub App installation id powering the binding, if any.</param>
/// <param name="Events">Webhook events to subscribe to. Null falls back to the connector's default set.</param>
/// <param name="Reviewer">
/// Default GitHub login (no leading <c>@</c>) requested as the reviewer on
/// pull requests opened by this unit. Optional — agents that pass a
/// reviewer explicitly still override per-call.
/// </param>
/// <param name="AddOnAssign">Labels to add when an issue is assigned through this unit.</param>
/// <param name="RemoveOnAssign">Labels to remove when an issue is assigned through this unit.</param>
/// <param name="IncludeLabels">Inbound webhook filter: labels that gate delivery (disjunctive). Issue #2407.</param>
/// <param name="ExcludeLabels">Inbound webhook filter: labels that drop delivery (evaluated first). Issue #2407.</param>
/// <param name="IncludeAuthors">Inbound webhook filter: GitHub logins that gate delivery (disjunctive). Issue #2407.</param>
/// <param name="IncludePaths">Inbound webhook filter: file-path prefixes that gate PR-shape delivery (disjunctive). Issue #2407.</param>
public record UnitGitHubConfigRequest(
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

/// <summary>
/// Response body for
/// <c>GET</c>/<c>PUT /api/v1/connectors/github/units/{unitId}/config</c>.
/// Returns the unit id and the effective config (with <see cref="Events"/>
/// resolved to the connector's defaults when the caller didn't supply one).
/// </summary>
/// <param name="UnitId">The unit id this config is bound to.</param>
/// <param name="Owner">The repository owner (user or organization login).</param>
/// <param name="Repo">The repository name.</param>
/// <param name="AppInstallationId">The GitHub App installation id powering the binding, if any.</param>
/// <param name="Events">The effective webhook event subscriptions.</param>
/// <param name="Reviewer">
/// Default reviewer login persisted on the binding, or <c>null</c> when
/// the unit didn't pick one. Surfaced verbatim — the response does not
/// invent a default.
/// </param>
/// <param name="EventsAreDefault">
/// <c>true</c> when the persisted binding has no explicit event list and
/// <see cref="Events"/> was materialized from the connector's defaults;
/// <c>false</c> when the operator picked an explicit set (even if that
/// set happens to coincide with the defaults). The portal's per-unit
/// connector tab reads this to decide whether the "Connector defaults"
/// toggle starts checked or unchecked, so an operator who explicitly
/// picks the default set is not silently flipped back into "use
/// defaults" mode on the next load. Mirror of the wizard request shape
/// where a <c>null</c> <see cref="UnitGitHubConfigRequest.Events"/>
/// already encodes "use defaults" intent. (#1146 / #1127)
/// </param>
/// <param name="AddOnAssign">Labels to add when an issue is assigned through this unit.</param>
/// <param name="RemoveOnAssign">Labels to remove when an issue is assigned through this unit.</param>
/// <param name="IncludeLabels">Inbound webhook filter: labels that gate delivery (disjunctive). Issue #2407.</param>
/// <param name="ExcludeLabels">Inbound webhook filter: labels that drop delivery (evaluated first). Issue #2407.</param>
/// <param name="IncludeAuthors">Inbound webhook filter: GitHub logins that gate delivery (disjunctive). Issue #2407.</param>
/// <param name="IncludePaths">Inbound webhook filter: file-path prefixes that gate PR-shape delivery (disjunctive). Issue #2407.</param>
public record UnitGitHubConfigResponse(
    string UnitId,
    string Owner,
    string Repo,
    long? AppInstallationId,
    IReadOnlyList<string> Events,
    string? Reviewer,
    bool EventsAreDefault,
    [property: JsonPropertyName("add_on_assign")] IReadOnlyList<string>? AddOnAssign = null,
    [property: JsonPropertyName("remove_on_assign")] IReadOnlyList<string>? RemoveOnAssign = null,
    [property: JsonPropertyName("include_labels")] IReadOnlyList<string>? IncludeLabels = null,
    [property: JsonPropertyName("exclude_labels")] IReadOnlyList<string>? ExcludeLabels = null,
    [property: JsonPropertyName("include_authors")] IReadOnlyList<string>? IncludeAuthors = null,
    [property: JsonPropertyName("include_paths")] IReadOnlyList<string>? IncludePaths = null);

/// <summary>
/// Response item for
/// <c>GET /api/v1/connectors/github/actions/list-installations</c>.
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
/// Response body for
/// <c>GET /api/v1/connectors/github/actions/install-url</c>.
/// </summary>
/// <param name="Url">The install URL the user should be sent to.</param>
public record GitHubInstallUrlResponse(string Url);

/// <summary>
/// Response item for
/// <c>GET /api/v1/connectors/github/actions/list-repositories</c>. One
/// entry per repository the GitHub App can currently see, aggregated
/// across every visible installation. The wizard renders the
/// <see cref="FullName"/> in a single dropdown, splits client-side, and
/// posts <see cref="Owner"/> + <see cref="Repo"/> + <see cref="InstallationId"/>
/// back on the typed config — replacing the v1-style three-control surface
/// (manual owner / manual repo / installation picker) with one selection.
/// </summary>
/// <param name="InstallationId">
/// The installation id the App authenticates as for this repo. Carried in
/// the response so the wizard never has to call back to resolve it from
/// owner+repo (the existing <c>FindInstallationForRepoAsync</c> path).
/// </param>
/// <param name="RepositoryId">The numeric repository id (informational; not persisted on the binding).</param>
/// <param name="Owner">The repository owner login.</param>
/// <param name="Repo">The repository short name.</param>
/// <param name="FullName">The <c>owner/name</c> combined form — the dropdown label.</param>
/// <param name="Private">Whether the repo is private — surfaced so the UI can render a lock icon.</param>
public record GitHubRepositoryResponse(
    long InstallationId,
    long RepositoryId,
    string Owner,
    string Repo,
    string FullName,
    bool Private);

/// <summary>
/// Response item for
/// <c>GET /api/v1/connectors/github/actions/list-collaborators</c>. The
/// reviewer-picker dropdown in the wizard binds to this — selecting a row
/// stores <see cref="Login"/> on <see cref="UnitGitHubConfigRequest.Reviewer"/>.
/// </summary>
/// <param name="Login">The GitHub login (without the leading <c>@</c>).</param>
/// <param name="AvatarUrl">The collaborator's avatar URL, if surfaced by GitHub.</param>
public record GitHubCollaboratorResponse(
    string Login,
    string? AvatarUrl);

/// <summary>
/// Structured body for the <c>401 Unauthorized</c> emitted by
/// <c>GET /api/v1/connectors/github/actions/list-repositories</c> when
/// the caller has no usable GitHub OAuth user session. The endpoint is
/// fail-closed (#1663): a session-less call MUST NOT fall back to the
/// installation-scoped list, because the resulting set leaks repos the
/// caller may not have permission to see in their own GitHub identity.
/// The portal renders the body as a "Link your GitHub account" panel
/// with a button that points at <see cref="AuthorizeUrl"/>.
/// </summary>
/// <param name="MissingOAuth">
/// Always <c>true</c>. Lets the portal key its remediation panel off a
/// single, stable extension field rather than parsing the title.
/// </param>
/// <param name="Reason">
/// A short, human-readable sentence the portal shows to the operator.
/// Distinguishes "no session id supplied", "session not found",
/// "access token missing", and "OAuth not configured on this
/// deployment" so an operator can act on the right gap.
/// </param>
/// <param name="AuthorizeUrl">
/// A GitHub OAuth authorize URL minted when <c>GitHub:OAuth</c> is
/// configured; <c>null</c> when it isn't. The portal uses this as a
/// configuration signal, then starts a fresh authorization request with
/// browser callback state when the operator clicks the link button.
/// </param>
/// <param name="State">
/// The state value bound to the authorize URL. Echoed back here so
/// callers that prefer not to parse the URL can match it against the
/// callback. Only set when <see cref="AuthorizeUrl"/> is.
/// </param>
public record GitHubMissingOAuthResponse(
    bool MissingOAuth,
    string Reason,
    string? AuthorizeUrl,
    string? State);
