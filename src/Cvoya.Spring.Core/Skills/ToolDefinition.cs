// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Skills;

using System.Text.Json;

/// <summary>
/// Represents a tool's JSON schema definition for use within a skill.
/// </summary>
/// <param name="Name">
/// The canonical, dotted-snake tool name (<c>&lt;namespace&gt;.&lt;tool_name&gt;</c>).
/// Validated at construction against <see cref="ToolNaming.Pattern"/>; invalid
/// ids throw <see cref="ArgumentException"/> so registries fail loudly at
/// registration time rather than silently shipping a malformed surface.
/// </param>
/// <param name="Description">A description of what the tool does.</param>
/// <param name="InputSchema">The JSON schema defining the tool's input parameters.</param>
public record ToolDefinition
{
    /// <summary>Creates the tool definition, enforcing the canonical name pattern.</summary>
    public ToolDefinition(string Name, string Description, JsonElement InputSchema)
        : this(Name, Description, InputSchema, Category: string.Empty)
    {
    }

    /// <summary>
    /// Creates the tool definition with an explicit
    /// <see cref="Category"/> (ADR-0056 §6). Existing call sites that
    /// pre-date the category surface keep using the three-argument
    /// constructor and surface as uncategorised (<see cref="Category"/>
    /// == <see cref="string.Empty"/>); the discovery tools treat
    /// uncategorised entries as belonging to no category.
    /// </summary>
    public ToolDefinition(string Name, string Description, JsonElement InputSchema, string Category)
    {
        if (!ToolNaming.IsValid(Name))
        {
            throw new ArgumentException(
                $"Tool name '{Name}' does not match the canonical pattern " +
                $"'{ToolNaming.Pattern}'. Tool ids must be lowercase, " +
                "dotted-snake, with a leading namespace segment " +
                "(e.g. 'github.create_issue', 'sv.directory.get_self').",
                nameof(Name));
        }
        this.Name = Name;
        this.Description = Description;
        this.InputSchema = InputSchema;
        this.Category = Category ?? string.Empty;
    }

    /// <summary>The canonical tool name.</summary>
    public string Name { get; init; }

    /// <summary>Human-readable description of what the tool does.</summary>
    public string Description { get; init; }

    /// <summary>JSON schema defining the tool's input parameters.</summary>
    public JsonElement InputSchema { get; init; }

    /// <summary>
    /// The leading namespace segment of <see cref="Name"/> (the portion before
    /// the first <c>.</c>). Computed from <see cref="Name"/> so the wire shape
    /// never carries a duplicate copy that could drift from the id.
    /// </summary>
    public string Namespace => ToolNaming.GetNamespace(Name);

    /// <summary>
    /// Capability category (ADR-0056 §6) — a short stable token used by
    /// the <c>sv.tools.list_categories</c> / <c>sv.tools.list</c>
    /// discovery surface to group tools by purpose
    /// (<c>messaging</c>, <c>directory</c>, <c>observability</c>,
    /// <c>tools</c>, …). Empty when the tool's registry has not opted
    /// into the category surface yet — those tools remain reachable
    /// directly but are not enumerated by category-based discovery.
    /// </summary>
    public string Category { get; init; } = string.Empty;
}
