// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Skills;

/// <summary>
/// Platform-default skill bundles that an agent created without explicit
/// skill grants (e.g. <c>spring agent create --name ... </c> with no
/// <c>--from-package</c> and no operator-supplied bundle list) should
/// inherit out of the box.
/// </summary>
/// <remarks>
/// <para>
/// Per ADR-0056 §8 ("Default skill bundle"), a fresh conversational
/// agent ships with the <c>sv.conversational.defaults</c> bundle so it
/// surfaces conversation-shaped guidance (and the <c>memory</c>
/// category grant) on top of the always-on platform-tool catalog and
/// the <c>### Platform Contract — Non-Negotiable</c> prompt fragment
/// (rendered inside <c>## Platform Instructions</c>, heading level per
/// #2738), both of which now live in Layer 1
/// (<see cref="IPlatformPromptProvider"/>) since #2670 and are
/// delivered to every agent regardless of equipped bundles. Rather
/// than encoding the bundle name as a magic string in the agent-create
/// endpoint, the platform's default-bundle list lives here so test
/// compositions (which substitute the bundle store) can reference the
/// same constant the production path uses.
/// </para>
/// <para>
/// The constants here are platform contract: a deployment that wants
/// to suppress the default conversational bundle (e.g. a tenant that
/// composes its own conversational guidance on top of the
/// platform-layer prompt) does so by passing an empty override into
/// <see cref="ConversationalDefaults"/>'s consumer, not by editing
/// this file.
/// </para>
/// </remarks>
public static class DefaultAgentSkillBundles
{
    /// <summary>
    /// The <c>sv.conversational.defaults</c> bundle reference
    /// (ADR-0056 §8 / #2657). Resolves to
    /// <c>packages/conversational-defaults/skills/conversational-defaults/</c>
    /// via the canonical <c>spring-voyage/</c> namespace prefix the
    /// file-system resolver strips before disk lookup.
    /// </summary>
    public static readonly SkillBundleReference ConversationalDefaults =
        new(Package: "spring-voyage/conversational-defaults", Skill: "conversational-defaults");

    /// <summary>
    /// The list of bundles a fresh agent inherits when no explicit
    /// skill grants were supplied at create time. Iteration order is
    /// preserved in the agent's equipped-bundle list — the first entry
    /// renders first in Layer 4 of the assembled prompt.
    /// </summary>
    public static readonly IReadOnlyList<SkillBundleReference> ForFreshAgent =
        new[] { ConversationalDefaults };
}
