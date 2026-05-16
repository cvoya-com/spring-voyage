// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Prompts;

using System.Text;

using Cvoya.Spring.Core.Skills;

/// <summary>
/// Builds the agent-instructions layer (Layer 4) from agent state — the
/// user-authored instructions plus any package-level skill bundles
/// equipped directly on the agent subject (#2360).
/// </summary>
/// <remarks>
/// <para>
/// The bundle markdown shape mirrors <see cref="UnitContextBuilder"/>'s
/// Layer 2 rendering for visual consistency: a single <c>### Skill
/// Bundles</c> heading followed by one <c>#### {PackageName}/{SkillName}</c>
/// sub-heading per bundle, the bundle prompt, and an optional <c>Required
/// tools:</c> sub-section.
/// </para>
/// <para>
/// Two distinct render paths exist intentionally: Layer 2 carries the
/// **unit's** bundles (inherited by every member agent through the unit-
/// context layer), Layer 4 carries the **agent's own** bundles. Member
/// agents see both without an explicit inheritance table — see
/// <c>docs/concepts/skills.md</c> for the model.
/// </para>
/// </remarks>
public class AgentInstructionsBuilder
{
    /// <summary>
    /// Builds the agent-instructions string. Returns an empty string when
    /// both inputs are empty so the caller can omit the
    /// <c>## Agent Instructions</c> section entirely.
    /// </summary>
    /// <param name="instructions">User-authored agent instructions, or <c>null</c>.</param>
    /// <param name="bundles">
    /// Optional ordered list of package-level skill bundles equipped on
    /// the agent. Declaration order is preserved in the output.
    /// </param>
    public string Build(
        string? instructions,
        IReadOnlyList<SkillBundle>? bundles)
    {
        var builder = new StringBuilder();

        if (!string.IsNullOrWhiteSpace(instructions))
        {
            builder.AppendLine(instructions.TrimEnd());
        }

        if (bundles is { Count: > 0 })
        {
            if (builder.Length > 0)
            {
                builder.AppendLine();
            }

            builder.AppendLine("### Skill Bundles");
            foreach (var bundle in bundles)
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
