// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Skills;

using System.Text.Json;

using Cvoya.Spring.Core.Capabilities;
using Cvoya.Spring.Core.Messaging;

/// <summary>
/// One resolved entry in the expertise-directory-driven skill catalog (#359).
/// Pairs a <see cref="ToolDefinition"/> (what the skill surface advertises)
/// with the <see cref="ExpertiseEntry"/> it came from (who actually holds the
/// expertise) so the invoker can translate a call by name back to the
/// concrete target.
/// </summary>
/// <remarks>
/// The skill name is directory-keyed (<c>expertise/{slug}</c>) — agent names
/// never appear in the skill surface. That keeps the catalog stable across
/// agent churn: swapping the agent that holds an expertise entry does not
/// rename the skill, and a capability projected at the unit boundary is
/// addressed the same way a leaf-agent capability is.
/// </remarks>
/// <param name="SkillName">Catalog-addressable skill name.</param>
/// <param name="Tool">Tool definition surfaced to callers (name + description + input schema).</param>
/// <param name="Target">
/// Concrete <see cref="Address"/> to dispatch to. For leaf-agent expertise
/// this is the agent origin; for unit-projected expertise this is the unit
/// that owns the projection.
/// </param>
/// <param name="Entry">The expertise entry this skill projects.</param>
public record ExpertiseSkill(
    string SkillName,
    ToolDefinition Tool,
    Address Target,
    ExpertiseEntry Entry);

/// <summary>
/// Naming helpers for the expertise-driven skill catalog (#359). Pulled into
/// a static class so callers that only need to derive a skill name from a
/// domain (e.g. tests, UI catalogs, future A2A gateway) don't have to take
/// a dependency on the registry or invoker.
/// </summary>
public static class ExpertiseSkillNaming
{
    /// <summary>
    /// Catalog prefix for expertise-driven skills. Chosen so (a) agent names
    /// never appear in the skill surface and (b) the prefix is unambiguously
    /// tied to the expertise directory (not to the agent roster or a unit
    /// path). Rationale lives in the closing issue (#540) and the
    /// <c>agent-runtime.md</c> skill-registries section.
    /// </summary>
    public const string Prefix = "expertise/";

    /// <summary>
    /// Derives the catalog skill name for an expertise domain. The slug is a
    /// case-folded, path-safe projection of <see cref="ExpertiseDomain.Name"/>.
    /// </summary>
    public static string GetSkillName(ExpertiseDomain domain)
    {
        ArgumentNullException.ThrowIfNull(domain);
        return Prefix + Slugify(domain.Name);
    }

    /// <summary>
    /// Lowercases the domain name and replaces any non-slug character with
    /// <c>-</c>, collapsing runs so <c>python/fastapi</c> → <c>python-fastapi</c>
    /// and <c>React / Next.js</c> → <c>react-next-js</c>. Empty input yields
    /// the empty string — callers that care about that case must guard before
    /// calling.
    /// </summary>
    public static string Slugify(string name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return string.Empty;
        }

        var buffer = new System.Text.StringBuilder(name.Length);
        var lastWasSeparator = true; // suppress leading separators
        foreach (var ch in name)
        {
            if (char.IsAsciiLetterOrDigit(ch))
            {
                buffer.Append(char.ToLowerInvariant(ch));
                lastWasSeparator = false;
            }
            else if (!lastWasSeparator)
            {
                buffer.Append('-');
                lastWasSeparator = true;
            }
        }

        // Trim any trailing separator.
        while (buffer.Length > 0 && buffer[^1] == '-')
        {
            buffer.Length--;
        }

        return buffer.ToString();
    }

    /// <summary>
    /// Attempts to parse a schema string into a <see cref="JsonElement"/> suitable
    /// for surfacing on a <see cref="ToolDefinition"/>. Invalid JSON falls back to
    /// the empty schema shape so the registry never advertises a malformed tool.
    /// </summary>
    public static JsonElement ParseSchemaOrEmpty(string? schemaJson)
    {
        if (!string.IsNullOrWhiteSpace(schemaJson))
        {
            try
            {
                using var doc = JsonDocument.Parse(schemaJson);
                return doc.RootElement.Clone();
            }
            catch (JsonException)
            {
                // Malformed schema — fall through to the empty object below.
            }
        }

        using var empty = JsonDocument.Parse("{\"type\":\"object\",\"properties\":{}}");
        return empty.RootElement.Clone();
    }
}
