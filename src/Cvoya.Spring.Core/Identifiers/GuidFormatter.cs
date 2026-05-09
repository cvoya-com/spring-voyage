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
