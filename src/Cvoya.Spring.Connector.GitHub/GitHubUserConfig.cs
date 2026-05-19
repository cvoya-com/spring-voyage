// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connector.GitHub;

using System.Text.Json.Serialization;

/// <summary>
/// Per-<c>TenantUser</c> GitHub display-identity configuration. Captures the
/// connector-side login the platform renders when this tenant user appears
/// in <c>@</c>-mentions on PR comments, in <c>--add-reviewer</c> invocations
/// against the GitHub API, and in attribution surfaces.
/// </summary>
/// <remarks>
/// <para>
/// Governed by
/// <see href="https://github.com/cvoya-com/spring-voyage/blob/main/docs/decisions/0047-platform-user-human-split.md">ADR-0047</see>
/// §4. The shape is <strong>strictly display identity</strong>: there is no
/// PAT field, no App-installation override, no token of any kind on this
/// record. Outbound GitHub credentials live on the unit binding row per
/// ADR-0047 §11; pinning auth on the per-tenant-user identity would
/// re-introduce the "calling tenant user disambiguates auth at use-time"
/// shape the ADR explicitly rejects (§§ 5–6).
/// </para>
/// <para>
/// This is <strong>not</strong> the binding's config shape — that role
/// belongs to <see cref="UnitGitHubConfig"/>, which carries the qualified
/// <c>owner/repo</c> addressing field and the <c>app_installation_id</c> /
/// <c>pat_secret_name</c> auth-choice fields. <see cref="GitHubUserConfig"/>
/// is tenant-user-scoped, not binding-scoped, and answers "who is this SV
/// tenant user in GitHub terms?" — never "what credential does the unit
/// push with?".
/// </para>
/// </remarks>
/// <param name="Username">
/// The GitHub login (without the leading <c>@</c>). Required — this is the
/// row's single load-bearing field and the value every display / mention /
/// reviewer-assignment site reads.
/// </param>
/// <param name="DisplayHandle">
/// Optional human-friendly rendering (e.g. <c>"Alice Smith (@alice)"</c>).
/// When <c>null</c>, render sites fall back to <see cref="Username"/>.
/// </param>
public record GitHubUserConfig(
    string Username,
    [property: JsonPropertyName("display_handle")] string? DisplayHandle = null);
