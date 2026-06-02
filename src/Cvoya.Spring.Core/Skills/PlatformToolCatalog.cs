// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Skills;

/// <summary>
/// The single authoritative catalog of the platform's <c>sv.*</c>
/// capability categories — one <see cref="PlatformToolCategory"/> per
/// <see cref="ToolCategories"/> token, carrying the one-line
/// <see cref="PlatformToolCategory.Summary"/> the discovery surface
/// returns from <c>sv.tools.list_categories</c> and the longer
/// <see cref="PlatformToolCategory.UsageGuidance"/> string it returns
/// from <c>sv.tools.list(category)</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this is the source of truth.</b> The category summaries and
/// usage-guidance prose used to live as private static data on the
/// <c>SvToolsDiscoverySkillRegistry</c> in <c>Cvoya.Spring.Dapr</c>,
/// invisible to anything that could not take a dependency on the
/// dispatcher project — including the user-facing docs. They were
/// therefore re-authored by hand in
/// <c>docs/reference/platform-tools.md</c> and paraphrased again in the
/// platform-prompt layer, and the three copies drifted (#2988 / F3 of
/// #2986: the observability guidance omitted
/// <c>sv.runtime.report_decision</c> and the directory guidance omitted
/// the six <c>sv.directory.*</c> expansion tools). Hoisting the prose to
/// a dependency-free type in <c>Cvoya.Spring.Core</c> lets every
/// consumer read from one place:
/// </para>
/// <list type="bullet">
///   <item><description>The runtime-facing discovery surface
///   (<c>SvToolsDiscoverySkillRegistry</c>) reads
///   <see cref="ByToken"/> directly — so what an agent sees from
///   <c>sv.tools.list</c> is this prose verbatim.</description></item>
///   <item><description>The user-facing catalog
///   (<c>docs/reference/platform-tools.md</c>) embeds the same
///   <see cref="PlatformToolCategory.UsageGuidance"/> strings, pinned by
///   a CI test that fails the build if the doc and this catalog
///   diverge.</description></item>
/// </list>
/// <para>
/// <b>Scope.</b> This catalog owns the <em>category-level</em> prose
/// only. The per-tool names, descriptions, and category stamps remain
/// authoritative on each tool's <see cref="ToolDefinition"/> in its
/// owning <see cref="ISkillRegistry"/>, already CI-pinned against the
/// catalog doc. A separate test pins that every tool a category
/// enumerates is referenced by name in that category's guidance, so the
/// guidance cannot fall behind the registered tool set again.
/// </para>
/// <para>
/// <b>Resilience to tool-set churn.</b> The guidance strings describe a
/// category's purpose and name its tools, but no consumer derives the
/// <em>set</em> of tools in a category from this prose — the live
/// registry stamps remain the only enumeration source. A category whose
/// tool membership changes (a tool added, or removed — e.g. the
/// <c>sv.expertise.*</c> tools folded into directory discovery) needs
/// only its guidance prose updated here; nothing keys off the prose
/// structurally.
/// </para>
/// </remarks>
public static class PlatformToolCatalog
{
    /// <summary>
    /// The canonical category catalog, in stable presentation order
    /// (messaging, directory, observability, tools, memory) — the order
    /// the user-facing doc and the discovery surface present categories
    /// in. Adding a platform category means adding a constant to
    /// <see cref="ToolCategories"/> and an entry here.
    /// </summary>
    public static IReadOnlyList<PlatformToolCategory> Categories { get; } =
    [
        new(
            ToolCategories.Messaging,
            "Send a one-way message to humans, agents, or units.",
            "Use sv.messaging.send to deliver a message to one or more " +
            "humans, agents, or units; every recipient lands on a single " +
            "shared thread with the caller. Use sv.messaging.multicast to " +
            "deliver the same message to several recipients, each on its " +
            "own independent 1-1 thread with the caller (or to a resolved " +
            "scope: unit-members, siblings). Use sv.messaging.respond_to to " +
            "continue an existing conversation — the platform delivers to " +
            "everyone already on the thread a message_id belongs to (minus " +
            "the caller). Valid recipient kinds are human, agent, and unit; " +
            "connector addresses appear on inbound messages as a sender but " +
            "are non-routable and are rejected synchronously with an " +
            "UnroutableTarget error. Delivery is one-way (ADR-0049): each " +
            "call returns a delivery acknowledgement; any response from a " +
            "recipient arrives later as a separate inbound message.",
            OwnedNamespaces: ["sv.messaging"]),
        new(
            ToolCategories.Directory,
            "Look up agents, units, and humans by address, role, or expertise.",
            "Use sv.directory.lookup when you already know an address (for " +
            "example the sender of the inbound message) and need the entry's " +
            "role / expertise / status. Use sv.directory.list to enumerate " +
            "members of a unit, the caller's siblings, or peers matching a " +
            "role or expertise filter. To walk the unit hierarchy " +
            "explicitly, use sv.directory.get_self for the calling entity, " +
            "sv.directory.get_member for a single entity by uuid, " +
            "sv.directory.list_members for a unit's direct members, " +
            "sv.directory.get_siblings for entities sharing a parent, " +
            "sv.directory.get_parents for an entity's parents, and " +
            "sv.directory.get_status for an entity's advisory runtime-status " +
            "snapshot. Every entry carries enough to act on (address, " +
            "display name, role, expertise, advisory live status) — feed an " +
            "address back into sv.messaging.send to reach the entry.",
            // sv.expertise.* is grouped into this category for discovery but is
            // deliberately NOT an owned namespace: it is dynamic per tenant and
            // being folded into directory discovery (#2989), so the guidance is
            // not coupled to it.
            OwnedNamespaces: ["sv.directory"]),
        new(
            ToolCategories.Observability,
            "Emit progress and decision signals operators can see live.",
            "Use sv.progress.report to publish a narrative progress beat with " +
            "an optional 0..1 fraction so a long-running turn is not silent " +
            "until completion. Use sv.runtime.report_decision to record a " +
            "structured routing / delegation decision so the choice is " +
            "visible on the activity stream. The platform records these as " +
            "RuntimeProgress and DecisionMade activities visible in the " +
            "portal and CLI live-tail.",
            OwnedNamespaces: ["sv.progress", "sv.runtime"]),
        new(
            ToolCategories.Tools,
            "Discover capability categories and their full tool definitions.",
            "Call sv.tools.list_categories on startup to see what your " +
            "tool surface contains beyond the fundamental core, then call " +
            "sv.tools.list(category) to retrieve the full tool definitions " +
            "(name + description + JSON input schema) and category-level " +
            "usage guidance for any category you need to act through.",
            OwnedNamespaces: ["sv.tools"]),
        new(
            ToolCategories.Memory,
            "Private memory and shared participant-set history.",
            "Two surfaces on the same category. Private memory: use " +
            "sv.memory.add to record agent-scoped entries recalled across all " +
            "your conversations (the default) or thread-scoped notes recalled " +
            "only within the current conversation (scope='thread'); " +
            "sv.memory.get to read one entry by id; sv.memory.list / " +
            "sv.memory.search to retrieve; sv.memory.update / " +
            "sv.memory.delete to mutate. Caller-scoped — another agent's " +
            "entries are not visible. Shared history: sv.memory.engagements " +
            "lists the participant sets you share a timeline with " +
            "(most-recent activity first); " +
            "sv.memory.history_with(participants=[…]) fetches the full " +
            "timeline shared with a participant set (your own address is " +
            "auto-included — do not list yourself); " +
            "sv.memory.search_messages free-text-searches across the " +
            "timelines you participate in, optionally scoped to a single " +
            "participant set. The agent never names a thread_id — the " +
            "participant set identifies the timeline.",
            OwnedNamespaces: ["sv.memory"]),
    ];

    /// <summary>
    /// Index of <see cref="Categories"/> keyed by the canonical token,
    /// for O(1) lookup by consumers that resolve a single category
    /// (the discovery surface, given a <c>category</c> argument).
    /// </summary>
    public static IReadOnlyDictionary<string, PlatformToolCategory> ByToken { get; } =
        Categories.ToDictionary(c => c.Token, StringComparer.Ordinal);
}

/// <summary>
/// One platform capability category: its canonical token, the one-line
/// summary <c>sv.tools.list_categories</c> returns, and the extended
/// usage-guidance string <c>sv.tools.list(category)</c> returns. See
/// <see cref="PlatformToolCatalog"/> for the single-source-of-truth
/// rationale.
/// </summary>
/// <param name="Token">
/// The canonical category token (the value tools stamp on
/// <see cref="ToolDefinition.Category"/>); one of the constants on
/// <see cref="ToolCategories"/>.
/// </param>
/// <param name="Summary">
/// One-line summary surfaced by <c>sv.tools.list_categories</c>.
/// </param>
/// <param name="UsageGuidance">
/// Extended, terse-clause-per-tool guidance surfaced by
/// <c>sv.tools.list(category)</c> describing when to reach for each tool
/// the category enumerates.
/// </param>
/// <param name="OwnedNamespaces">
/// The tool namespaces (the segment before the first <c>.</c>, e.g.
/// <c>sv.directory</c>) the category is defined around and whose every
/// statically-registered tool the <see cref="UsageGuidance"/> MUST name.
/// This is the contract the drift-guard test enforces (#2988): a tool in
/// an owned namespace that the guidance omits fails the build. A category
/// may group tools from <em>other</em> namespaces too (for discovery
/// convenience) without being forced to enumerate them — e.g. the
/// <c>directory</c> category groups the dynamic / transitional
/// <c>sv.expertise.*</c> tools, which are not an owned namespace, so the
/// guidance is not coupled to them and tolerates their removal (#2989).
/// </param>
public sealed record PlatformToolCategory(
    string Token,
    string Summary,
    string UsageGuidance,
    IReadOnlyList<string> OwnedNamespaces);
