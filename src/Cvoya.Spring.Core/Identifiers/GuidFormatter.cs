// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Identifiers;

using System.Diagnostics.CodeAnalysis;

/// <summary>
/// Canonical formatter / parser for stable Guid identifiers on every public
/// surface (URLs, JSON DTOs, manifest references, log entries).
///
/// <para>
/// <b>Wire format.</b> All public surfaces emit Guids in <b>no-dash 32-char
/// lowercase hex</b> form (<c>Guid.ToString("N")</c>). Parsers accept both
/// the no-dash form and the conventional dashed form (<c>Guid.TryParse</c>
/// is lenient). The "emit one form, parse many" rule keeps copy-paste
/// workflows working while eliminating rendering ambiguity at the source.
/// </para>
/// </summary>
public static class GuidFormatter
{
    /// <summary>
    /// Returns the canonical wire form for the given Guid: 32-character
    /// lowercase hex, no dashes, no braces.
    /// </summary>
    public static string Format(Guid value) => value.ToString("N");

    /// <summary>
    /// Returns the standard 8-4-4-4-12 dashed UUID form for handing
    /// identifiers to external systems whose parsers reject the no-dash
    /// <see cref="Format"/> output. Spring Voyage's internal surfaces
    /// (URLs, addresses, storage, logs) keep using <see cref="Format"/>;
    /// this helper is only for the platform-to-external boundary.
    /// </summary>
    /// <remarks>
    /// Motivating case: Claude Code's <c>--session-id &lt;uuid&gt;</c> flag
    /// (and other CLI agents the bridge wires per ADR-0041) requires the
    /// standard dashed UUID form and rejects the no-dash variant with
    /// <c>Error: Invalid session ID. Must be a valid UUID.</c>.
    /// </remarks>
    public static string FormatExternal(Guid value) => value.ToString("D");

    /// <summary>
    /// Attempts to parse a Guid from a string, accepting any of the
    /// conventional forms (<c>N</c>, <c>D</c>, <c>B</c>, <c>P</c>, <c>X</c>).
    /// Returns <c>false</c> for null, whitespace, or unparseable input.
    /// </summary>
    public static bool TryParse([NotNullWhen(true)] string? value, out Guid result)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            result = Guid.Empty;
            return false;
        }

        return Guid.TryParse(value, out result);
    }
}
