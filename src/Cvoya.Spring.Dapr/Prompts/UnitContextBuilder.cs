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
    /// <returns>The formatted unit context string, or an empty string if all inputs are empty.</returns>
    public string Build(IReadOnlyList<Address> members, JsonElement? policies, IReadOnlyList<Skill>? skills)
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

        return builder.ToString().TrimEnd();
    }
}