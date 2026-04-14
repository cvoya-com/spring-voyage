// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Prompts;

using System.Text;
using System.Text.Json;

using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Core.Skills;

/// <summary>
/// Builds the unit context layer (Layer 2) from unit state including
/// peer directory, policies, and skill descriptions.
/// </summary>
public class UnitContextBuilder
{
    /// <summary>
    /// Builds the unit context string from the provided unit state.
    /// </summary>
    /// <param name="members">The addresses of peer agents in the unit.</param>
    /// <param name="policies">Optional unit policies as a JSON element.</param>
    /// <param name="skills">Optional skills available to the agent.</param>
    /// <param name="skillBundles">
    /// Optional package-level skill bundles resolved from the unit manifest
    /// (see #167). Rendered after the connector-skills section so the final
    /// layer-2 ordering is peer directory → policies → available skills →
    /// skill bundles. Concatenation order within the section follows the
    /// declaration order in the manifest.
    /// </param>
    /// <returns>The formatted unit context string, or an empty string if all inputs are empty.</returns>
    public string Build(
        IReadOnlyList<Address> members,
        JsonElement? policies,
        IReadOnlyList<Skill>? skills,
        IReadOnlyList<SkillBundle>? skillBundles = null)
    {
        var builder = new StringBuilder();

        if (members.Count > 0)
        {
            builder.AppendLine("### Peer Directory");
            foreach (var member in members)
            {
                builder.AppendLine($"- {member.Scheme}://{member.Path}");
            }

            builder.AppendLine();
        }

        if (policies is { ValueKind: not JsonValueKind.Null and not JsonValueKind.Undefined })
        {
            builder.AppendLine("### Policies");
            builder.AppendLine(policies.Value.ToString());
            builder.AppendLine();
        }

        if (skills is { Count: > 0 })
        {
            builder.AppendLine("### Available Skills");
            foreach (var skill in skills)
            {
                builder.AppendLine($"- **{skill.Name}**: {skill.Description}");

                foreach (var tool in skill.Tools)
                {
                    builder.AppendLine($"  - Tool: {tool.Name} — {tool.Description}");
                }
            }

            builder.AppendLine();
        }

        // Package-level skill bundles. Declaration order is preserved so the
        // operator's manifest layout determines prompt-fragment ordering. A
        // prompt-only bundle (no tools) still contributes its prompt.
        if (skillBundles is { Count: > 0 })
        {
            builder.AppendLine("### Skill Bundles");
            foreach (var bundle in skillBundles)
            {
                builder.AppendLine($"#### {bundle.PackageName}/{bundle.SkillName}");
                builder.AppendLine(bundle.Prompt.TrimEnd());
                if (bundle.RequiredTools.Count > 0)
                {
                    builder.AppendLine();
                    builder.AppendLine("Required tools:");
                    foreach (var tool in bundle.RequiredTools)
                    {
                        var optionalTag = tool.Optional ? " (optional)" : string.Empty;
                        builder.AppendLine($"- {tool.Name}{optionalTag}: {tool.Description}");
                    }
                }
                builder.AppendLine();
            }
        }

        return builder.ToString().TrimEnd();
    }
}