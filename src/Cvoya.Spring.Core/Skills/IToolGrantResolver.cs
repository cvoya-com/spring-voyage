// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Skills;

using Cvoya.Spring.Core.Messaging;

/// <summary>
/// Resolver that returns the flattened effective tool set for a subject
/// (agent or unit), merging the four provenance tiers (#2335 Sub B).
/// The implementation walks, in order:
/// <list type="number">
///   <item><description>Implicit platform tools — every <c>sv.*</c>
///   tool registered with the platform's <see cref="ISkillRegistry"/>
///   set, surfaced with <see cref="EffectiveTool.Provenance"/> =
///   <c>"platform"</c>. No row required.</description></item>
///   <item><description>Connector grants — every
///   <c>&lt;ToolNamespace&gt;.*</c> tool from each connector the
///   subject is bound to (directly or through unit inheritance),
///   surfaced with <c>provenance = "connector:&lt;Slug&gt;"</c>.</description></item>
///   <item><description>Image tools — the SDK-declared
///   <c>image_tools</c> column on the agent/unit definition (added by
///   Sub C #2336). Read defensively — when the column is absent or
///   null, the resolver yields nothing for this tier rather than
///   throwing. Surfaced with <c>provenance = "image:&lt;digest&gt;"</c>
///   (digest empty until Sub C lands).</description></item>
///   <item><description>Explicit grants — rows in
///   <c>agent_tool_grants</c> / <c>unit_tool_grants</c>, surfaced with
///   <c>provenance = "explicit"</c>.</description></item>
/// </list>
/// </summary>
/// <remarks>
/// <para>
/// Tools that appear in more than one tier are reported once; the
/// returned <see cref="EffectiveTool.Provenance"/> is the highest-
/// precedence source per the resolution order: explicit &gt;
/// connector &gt; platform &gt; image. The precedence is informational
/// for v0.1 (per-tool deny is #2333, v0.2) — encoded cleanly here so
/// the future deny logic slots in without a contract change.
/// </para>
/// <para>
/// Lives in <c>Cvoya.Spring.Core</c> so call sites (the agent runtime,
/// the MCP server, OpenAPI surfaces) can take a dependency on the
/// abstraction without pulling in the EF implementation. The cloud
/// overlay swaps the implementation through <c>TryAdd*</c>.
/// </para>
/// </remarks>
public interface IToolGrantResolver
{
    /// <summary>
    /// Returns the flat list of tools effectively granted to
    /// <paramref name="subject"/>, merged across all provenance tiers.
    /// Empty when the subject has no grants at all (and no <c>sv.*</c>
    /// tools are registered — practically only in unit tests).
    /// </summary>
    /// <param name="subject">
    /// Address of the agent or unit to resolve grants for. The scheme
    /// must be <see cref="Address.AgentScheme"/> or
    /// <see cref="Address.UnitScheme"/>.
    /// </param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    Task<IReadOnlyList<EffectiveTool>> ResolveAsync(
        Address subject,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Provenance discriminator values returned in
/// <see cref="EffectiveTool.Provenance"/>. Connector and image
/// provenance carry the concrete slug / digest in the suffix:
/// <c>"connector:github"</c>, <c>"image:sha256:…"</c>.
/// </summary>
public static class ToolProvenance
{
    /// <summary>Implicit platform-tier provenance for <c>sv.*</c> tools.</summary>
    public const string Platform = "platform";

    /// <summary>Provenance prefix for connector-bound tools (e.g. <c>connector:github</c>).</summary>
    public const string ConnectorPrefix = "connector:";

    /// <summary>Provenance prefix for image-declared tools (e.g. <c>image:sha256:…</c>).</summary>
    public const string ImagePrefix = "image:";

    /// <summary>Provenance for operator-set rows on the tool-grants tables.</summary>
    public const string Explicit = "explicit";
}

/// <summary>
/// One row in the effective tool set returned by
/// <see cref="IToolGrantResolver.ResolveAsync"/>.
/// </summary>
/// <param name="Name">
/// Canonical tool id, e.g. <c>github.create_issue</c>. Matches
/// <see cref="ToolDefinition.Name"/>.
/// </param>
/// <param name="Namespace">
/// Namespace segment — the portion of <see cref="Name"/> before the
/// first <c>.</c>. Surfaced separately so callers can group by
/// namespace without re-parsing the id.
/// </param>
/// <param name="Description">
/// Human-readable tool description, mirroring
/// <see cref="ToolDefinition.Description"/>. Empty for image-tier
/// tools that have not yet been introspected.
/// </param>
/// <param name="Provenance">
/// Effective provenance after precedence resolution. One of
/// <see cref="ToolProvenance.Platform"/>,
/// <see cref="ToolProvenance.Explicit"/>, or the prefixed forms
/// <see cref="ToolProvenance.ConnectorPrefix"/> /
/// <see cref="ToolProvenance.ImagePrefix"/>.
/// </param>
/// <param name="InheritedFromUnitName">
/// Human-readable display name of the unit the grant was inherited
/// from. <c>null</c> when the grant is set directly on the subject
/// (no inheritance walk required). When the same tool surfaces on
/// the subject directly and also through inheritance, the direct
/// row wins and this field is <c>null</c>.
/// </param>
public sealed record EffectiveTool(
    string Name,
    string Namespace,
    string Description,
    string Provenance,
    string? InheritedFromUnitName);
