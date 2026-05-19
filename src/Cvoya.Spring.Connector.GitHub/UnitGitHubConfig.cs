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
/// <remarks>
/// <para>
/// ADR-0047 §11 reshape:
/// <list type="bullet">
///   <item><description>
///     <c>Owner</c> dropped — structurally redundant once <see cref="Repo"/>
///     carries the qualified <c>owner/repo</c> form (CLI / portal reject
///     unqualified inputs at parse time per Phase G + Phase H).
///   </description></item>
///   <item><description>
///     <see cref="PatSecretName"/> added — the alternative auth path for
///     bindings that don't go through a GitHub App installation (e.g. a
///     public-repo flow with a PAT). Stored as a tenant secret name per
///     ADR-0003; the binding row never holds the token value itself.
///   </description></item>
///   <item><description>
///     <see cref="AppInstallationId"/> retained — exactly one of
///     <see cref="AppInstallationId"/> and <see cref="PatSecretName"/>
///     MUST be set at binding-create time. The binding-create endpoint
///     enforces the gate with the structured codes
///     <c>GitHubBindingAuthRequired</c> (neither set) and
///     <c>GitHubBindingAuthAmbiguous</c> (both set).
///   </description></item>
/// </list>
/// </para>
/// </remarks>
/// <param name="Repo">
/// The qualified repository name in <c>owner/repo</c> form (e.g.
/// <c>cvoya-com/spring-voyage</c>). Per ADR-0047 §11 the binding's
/// addressing concern is the qualified pair, not the owner and repo
/// names as separate columns.
/// </param>
/// <param name="AppInstallationId">
/// The GitHub App installation id selected when binding this unit to a
/// repository through the SV App or a BYO App. Drives outbound auth via
/// the App-installation token-mint path of ADR-0047 §6. Set to non-null
/// when the operator chose the App-installation route at binding time;
/// <c>null</c> when the PAT route was chosen.
/// </param>
/// <param name="PatSecretName">
/// Free-form tenant-secret name addressing the PAT this binding pushes
/// with. Set to non-null when the operator chose the PAT route at
/// binding time; <c>null</c> when the App-installation route was chosen.
/// The default naming convention (per ADR-0047 §5) is
/// <c>binding/&lt;binding-id-no-dash&gt;/github/pat</c>; operators who
/// paste an existing secret name override the default at create time.
/// The resolver does not parse the secret name to recover the binding id.
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
    string Repo,
    long? AppInstallationId = null,
    [property: JsonPropertyName("pat_secret_name")] string? PatSecretName = null,
    IReadOnlyList<string>? Events = null,
    string? Reviewer = null,
    [property: JsonPropertyName("add_on_assign")] IReadOnlyList<string>? AddOnAssign = null,
    [property: JsonPropertyName("remove_on_assign")] IReadOnlyList<string>? RemoveOnAssign = null,
    [property: JsonPropertyName("include_labels")] IReadOnlyList<string>? IncludeLabels = null,
    [property: JsonPropertyName("exclude_labels")] IReadOnlyList<string>? ExcludeLabels = null,
    [property: JsonPropertyName("include_authors")] IReadOnlyList<string>? IncludeAuthors = null,
    [property: JsonPropertyName("include_paths")] IReadOnlyList<string>? IncludePaths = null)
{
    /// <summary>
    /// Returns <c>true</c> when <see cref="Repo"/> is a syntactically valid
    /// qualified <c>owner/repo</c> string and surfaces the two halves in
    /// the <paramref name="owner"/> / <paramref name="repoName"/> out
    /// parameters. Returns <c>false</c> when the value is null, empty, or
    /// missing the slash separator (e.g. a v0.0 row that pre-dates the
    /// ADR-0047 §11 reshape) — in which case both out parameters are set
    /// to <see cref="string.Empty"/>. Centralised here so every call site
    /// that needs the (owner, repo) split shares the same parse.
    /// </summary>
    public static bool TryParseRepo(string? qualifiedRepo, out string owner, out string repoName)
    {
        owner = string.Empty;
        repoName = string.Empty;
        if (string.IsNullOrWhiteSpace(qualifiedRepo))
        {
            return false;
        }
        var slash = qualifiedRepo.IndexOf('/');
        if (slash <= 0 || slash >= qualifiedRepo.Length - 1)
        {
            return false;
        }
        owner = qualifiedRepo[..slash];
        repoName = qualifiedRepo[(slash + 1)..];
        return true;
    }
}
