// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Prompts;

using System.Text;
using System.Text.Json;

using Cvoya.Spring.Core.Skills;

/// <summary>
/// Builds the unit context layer (Layer 2) from unit state — policies
/// and package-level skill bundles. The peer-directory rendering that
/// used to live here was removed in #2231 (composition is now queried
/// on demand via <c>sv.directory.*</c>); the per-registry skill listing
/// was removed in #2670 (the platform-tool catalog is rendered once in
/// Layer 1 by <see cref="PlatformPromptProvider"/>, and connector
/// instructions ride <c>IConnectorPromptContextResolver</c> via the
/// platform-layer connector-context section).
/// </summary>
public class UnitContextBuilder
{
    /// <summary>
    /// Builds the unit context string from the provided unit state.
    /// </summary>
    /// <param name="policies">Optional unit policies as a JSON element.</param>
    /// <param name="skillBundles">
    /// Optional package-level skill bundles resolved from the unit manifest
    /// (see #167). Rendered after policies so the final layer-2 ordering
    /// is policies → skill bundles. Concatenation order within the
    /// section follows the declaration order in the manifest.
    /// </param>
    /// <returns>The formatted unit context string, or an empty string if all inputs are empty.</returns>
    public string Build(
        JsonElement? policies,
        IReadOnlyList<SkillBundle>? skillBundles = null)
    {
        var builder = new StringBuilder();

        if (policies is { ValueKind: not JsonValueKind.Null and not JsonValueKind.Undefined })
        {
            builder.AppendLine("### Policies");
            builder.AppendLine(policies.Value.ToString());
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
