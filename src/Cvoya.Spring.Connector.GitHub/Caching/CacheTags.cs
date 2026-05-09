// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connector.GitHub.Caching;

/// <summary>
/// Canonical tag producers for cached GitHub responses. Read-side skills and
/// webhook-side invalidation must agree on the exact string shape, so every
/// producer lives here. GitHub treats PRs and issues as the same resource for
/// comments — <see cref="Issue"/> and <see cref="PullRequest"/> therefore use
/// distinct prefixes to avoid accidental cross-invalidation when only one
/// side actually changed.
/// </summary>
public static class CacheTags
{
    /// <summary>
    /// Repository-scope tag: invalidates every cached read pertaining to the
    /// repo regardless of the specific issue / PR number.
    /// </summary>
    public static string Repository(string owner, string repo) =>
        $"repo:{Normalize(owner)}/{Normalize(repo)}";

    /// <summary>
    /// Pull-request-scope tag: invalidates only PR-specific cached reads.
    /// </summary>
    public static string PullRequest(string owner, string repo, int number) =>
        $"pr:{Normalize(owner)}/{Normalize(repo)}#{number}";

    /// <summary>
    /// Issue-scope tag: invalidates issue-specific cached reads. Also used by
    /// <c>issue_comment</c> events because GitHub routes PR comments through
    /// the issue comment API — caches keyed on "comments" for PR number N
    /// register under both <see cref="Issue"/>(N) and <see cref="PullRequest"/>(N).
    /// </summary>
    public static string Issue(string owner, string repo, int number) =>
        $"issue:{Normalize(owner)}/{Normalize(repo)}#{number}";

    /// <summary>
    /// Owner-scope tag for the org-wide Projects v2 board list. Projects v2
    /// lives at the organization (or user) level rather than in a repository,
    /// so lists share a tag keyed only on the owner login. Invalidated by any
    /// <c>projects_v2</c> event (create / edit / close / reopen / delete) so
    /// a new or renamed board becomes visible immediately.
    /// </summary>
    public static string ProjectV2List(string owner) =>
        $"projects-v2-list:{Normalize(owner)}";

    /// <summary>
    /// Project-scope tag: invalidates cached reads of a single Projects v2
    /// board and its item page slices. Webhook derivation uses the owner +
    /// project number carried by <c>projects_v2</c> events. Mutation skills
    /// that alter the set of items on a board (add / archive / delete) also
    /// flush this tag; the board identifier is the human-visible
    /// <c>owner/number</c> pair, not the opaque GraphQL node id, so webhook-
    /// side producers (which see <c>projects_v2_item</c> payloads carrying
    /// <c>owner</c> and <c>project_number</c>) and skill-side producers
    /// (which receive them as optional arguments from the caller) agree on
    /// the same string.
    /// </summary>
    public static string ProjectV2(string owner, int number) =>
        $"project-v2:{Normalize(owner)}/{number}";

    /// <summary>
    /// Item-scope tag: invalidates a single cached Projects v2 item read.
    /// Keyed on the item's GraphQL node id (the <c>itemId</c> argument of
    /// <c>github_get_project_v2_item</c>), which is exactly what
    /// <c>projects_v2_item</c> webhooks carry as <c>node_id</c>. Emitted by
    /// field-value, archive, and delete mutations. Node ids are already
    /// globally unique, so no owner qualifier is required.
    /// </summary>
    public static string ProjectV2Item(string itemId) =>
        $"project-v2-item:{itemId}";

    // Casing normalization: GitHub repo slugs are case-insensitive for
    // lookup (the API redirects), so normalizing avoids two caches for
    // "Acme/repo" and "acme/repo".
    private static string Normalize(string s) => s.ToLowerInvariant();
}
