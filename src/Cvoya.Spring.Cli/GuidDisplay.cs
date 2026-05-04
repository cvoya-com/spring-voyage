// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Cli;

using System;

/// <summary>
/// CLI-side formatting helpers for nullable <see cref="Guid"/> values
/// returned by the Kiota client. After #1629 every public ID flipped from
/// <c>string</c> to <c>Guid</c>; the CLI's <c>OutputFormatter.Column</c>
/// expects nullable string getters, so each column lambda funnels through
/// <see cref="Format(Guid?)"/> to render the canonical 32-character no-dash
/// hex form (matching the wire shape and
/// <see cref="Cvoya.Spring.Core.Identifiers.GuidFormatter.Format"/>).
/// </summary>
public static class GuidDisplay
{
    /// <summary>
    /// Formats a nullable <see cref="Guid"/> as 32-character lowercase hex
    /// (no dashes). Returns <see langword="null"/> for null input so the
    /// formatter renders an empty cell.
    /// </summary>
    public static string? Format(Guid? value) => value?.ToString("N");

    /// <summary>
    /// Formats a non-nullable <see cref="Guid"/> as 32-character lowercase
    /// hex (no dashes).
    /// </summary>
    public static string Format(Guid value) => value.ToString("N");
}