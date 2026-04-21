// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Cli.Utilities;

using System.Globalization;

using Microsoft.Kiota.Abstractions.Serialization;

/// <summary>
/// Renders a Kiota <see cref="UntypedNode"/> scalar as a plain string for
/// human-facing output. OpenAPI 3.1 encodes several numeric fields as the
/// union <c>{ "type": ["integer", "string"] }</c>; Kiota models those as
/// <see cref="UntypedNode"/>, and calling <c>ToString()</c> on the concrete
/// subclass returns the full type name (<c>UntypedInteger</c>) instead of
/// the underlying value. Row builders should route such fields through
/// <see cref="FormatScalar"/> before placing them in a table cell.
/// </summary>
public static class UntypedNodeFormatter
{
    /// <summary>
    /// Returns the scalar string representation of <paramref name="node"/>,
    /// unwrapping the concrete <c>Untyped*</c> subclass. Returns an empty
    /// string for <see langword="null"/> and for Kiota's explicit null
    /// sentinel; returns the default <c>ToString()</c> for unknown
    /// subclasses (including <c>UntypedObject</c> and <c>UntypedArray</c>,
    /// which are not scalars).
    /// </summary>
    public static string FormatScalar(UntypedNode? node)
    {
        if (node is null)
        {
            return string.Empty;
        }

        return node switch
        {
            UntypedString s => s.GetValue() ?? string.Empty,
            UntypedBoolean b => b.GetValue() ? "true" : "false",
            UntypedInteger i => i.GetValue().ToString(CultureInfo.InvariantCulture),
            UntypedLong l => l.GetValue().ToString(CultureInfo.InvariantCulture),
            UntypedDouble d => d.GetValue().ToString(CultureInfo.InvariantCulture),
            UntypedFloat f => f.GetValue().ToString(CultureInfo.InvariantCulture),
            UntypedDecimal m => m.GetValue().ToString(CultureInfo.InvariantCulture),
            UntypedNull => string.Empty,
            _ => node.ToString() ?? string.Empty,
        };
    }
}