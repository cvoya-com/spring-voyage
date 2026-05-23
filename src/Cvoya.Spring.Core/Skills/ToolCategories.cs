// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Skills;

/// <summary>
/// Canonical capability categories (ADR-0056 §6) used by the
/// <c>sv.tools.list_categories</c> / <c>sv.tools.list</c> discovery
/// surface to group tools by purpose. The constants here are the only
/// category tokens the platform itself stamps on its built-in tools;
/// connector packages may declare their own (the discovery surface is
/// category-agnostic — any non-empty
/// <see cref="ToolDefinition.Category"/> value is enumerable).
/// </summary>
/// <remarks>
/// Tokens are short, lowercase, and stable. They are wire-visible —
/// runtimes pass them back into <c>sv.tools.list(category)</c> — so
/// renames are breaking changes against deployed runtimes. Add a new
/// constant rather than renaming.
/// </remarks>
public static class ToolCategories
{
    /// <summary>Reply on a thread / fan out to peers (<c>sv.messaging.*</c>).</summary>
    public const string Messaging = "messaging";

    /// <summary>Resolve members, siblings, peers by address / role / expertise (<c>sv.directory.*</c>).</summary>
    public const string Directory = "directory";

    /// <summary>Mid-turn progress / decision signals (<c>sv.progress.*</c>, <c>sv.runtime.report_decision</c>).</summary>
    public const string Observability = "observability";

    /// <summary>The discovery surface itself (<c>sv.tools.*</c>).</summary>
    public const string Tools = "tools";

    /// <summary>Cross-thread state — goals, notes, learned facts (<c>sv.memory.*</c>).</summary>
    public const string Memory = "memory";

    /// <summary>Capability-typed expertise tools (<c>sv.expertise.{slug}</c>).</summary>
    public const string Expertise = "expertise";
}
