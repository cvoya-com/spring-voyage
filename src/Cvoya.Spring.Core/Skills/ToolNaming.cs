// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Skills;

using System.Text.RegularExpressions;

/// <summary>
/// Canonical naming for tool identifiers (#2334). Every <see cref="ToolDefinition"/>
/// surfaced through an <see cref="ISkillRegistry"/> must use the dotted-snake
/// pattern <c>&lt;namespace&gt;.&lt;tool_name&gt;</c> — each segment is lowercase,
/// starts with a letter, and contains only letters, digits, and underscores.
/// The first segment carries the registry namespace (e.g. <c>sv</c>, <c>github</c>);
/// the segments after the first dot identify the tool inside that namespace.
/// </summary>
/// <remarks>
/// <para>
/// The pattern is enforced by <see cref="ToolDefinition"/>'s constructor so a
/// registry that accidentally re-introduces slash- or underscore-prefixed ids
/// fails loudly at registration rather than silently shipping inconsistent
/// surfaces. The structural regression test in the host suite enumerates every
/// DI-registered <see cref="ISkillRegistry"/> and re-checks the contract from
/// the outside.
/// </para>
/// </remarks>
public static class ToolNaming
{
    /// <summary>
    /// Canonical tool-name pattern: a leading namespace segment, one or more
    /// dot-separated lowercase snake_case segments. At least one dot is
    /// required so every id carries an explicit namespace.
    /// </summary>
    public static readonly Regex Pattern = new(
        @"^[a-z][a-z0-9_]*(\.[a-z][a-z0-9_]*)+$",
        RegexOptions.Compiled);

    /// <summary>
    /// Returns <c>true</c> when <paramref name="name"/> matches the canonical
    /// dotted-snake tool-name pattern. Returns <c>false</c> for null, empty,
    /// or otherwise non-conforming input.
    /// </summary>
    public static bool IsValid(string? name)
        => !string.IsNullOrEmpty(name) && Pattern.IsMatch(name);

    /// <summary>
    /// Returns the leading namespace segment of a canonical tool name (the
    /// portion before the first <c>.</c>). For non-conforming input the whole
    /// string is returned so callers can still produce a deterministic
    /// grouping key during diagnostics.
    /// </summary>
    public static string GetNamespace(string name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return string.Empty;
        }
        var dot = name.IndexOf('.');
        return dot <= 0 ? name : name[..dot];
    }
}
