// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Cli;

using System.Globalization;

using Microsoft.Kiota.Abstractions.Serialization;

/// <summary>
/// Helpers for unpacking Kiota's <see cref="UntypedNode"/> value shape.
/// </summary>
/// <remarks>
/// OpenAPI 3.1 marks <c>int32</c>/<c>int64</c> values as the union
/// <c>{ "type": ["integer", "string"], "format": "int64" }</c> — the JSON
/// schema's way of allowing a number-or-numeric-string. Kiota's codegen
/// models unions as <see cref="UntypedNode"/>, which forces every CLI call
/// site to unbox the concrete subclass (<c>UntypedLong</c>,
/// <c>UntypedInteger</c>, <c>UntypedString</c>, ...). These helpers
/// centralise the unboxing so commands don't each reinvent the switch.
/// </remarks>
public static class KiotaConversions
{
    /// <summary>
    /// Reads the <see cref="UntypedNode"/> as a 64-bit integer, falling back
    /// to 0 when the value is missing or malformed. Accepts the concrete
    /// subclasses Kiota emits for integer unions.
    /// </summary>
    public static long ToLong(UntypedNode? node)
    {
        if (node is null)
        {
            return 0L;
        }

        return node switch
        {
            UntypedLong longNode when longNode.GetValue() is long l => l,
            UntypedInteger intNode when intNode.GetValue() is int i => i,
            UntypedString stringNode when stringNode.GetValue() is string s
                && long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) => parsed,
            UntypedDouble doubleNode when doubleNode.GetValue() is double d => (long)d,
            UntypedDecimal decimalNode when decimalNode.GetValue() is decimal m => (long)m,
            _ => 0L,
        };
    }

    /// <summary>
    /// Reads the <see cref="UntypedNode"/> as a 32-bit integer, falling back
    /// to 0 when the value is missing or malformed.
    /// </summary>
    public static int ToInt(UntypedNode? node) => (int)ToLong(node);

    /// <summary>
    /// Reads the <see cref="UntypedNode"/> as a double, falling back to 0
    /// when the value is missing or malformed. Used for the wait-time
    /// duration fields that OpenAPI tags with format <c>double</c>.
    /// </summary>
    public static double ToDouble(UntypedNode? node)
    {
        if (node is null)
        {
            return 0d;
        }

        return node switch
        {
            UntypedDouble doubleNode when doubleNode.GetValue() is double d => d,
            UntypedDecimal decimalNode when decimalNode.GetValue() is decimal m => (double)m,
            UntypedLong longNode when longNode.GetValue() is long l => l,
            UntypedInteger intNode when intNode.GetValue() is int i => i,
            UntypedString stringNode when stringNode.GetValue() is string s
                && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) => parsed,
            _ => 0d,
        };
    }
}