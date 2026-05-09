// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Cli;

using Cvoya.Spring.Core.Identifiers;

/// <summary>
/// Parses CLI address arguments in the canonical wire form
/// <c>scheme:&lt;32-hex-no-dash&gt;</c> (e.g.
/// <c>agent:8c5fab2a8e7e4b9c92f1d8a3b4c5d6e7</c>) — see ADR-0036
/// (<c>docs/decisions/0036-single-identity-model.md</c>).
///
/// <para>
/// Lenient on input: accepts both the canonical no-dash form and the
/// dashed Guid form so copy-paste workflows continue to work.
/// </para>
///
/// <para>
/// The legacy <c>scheme://path</c> shape is no longer accepted — pre-#1637
/// the path component carried a slug, but the single-identity model
/// removed slugs from every persistence and routing surface. CLI surfaces
/// that take a name (e.g. <c>spring agent show alice</c>) accept the bare
/// name through <see cref="CliResolver"/>; this parser only handles the
/// scheme-prefixed form for direct addressing.
/// </para>
/// </summary>
public static class AddressParser
{
    /// <summary>
    /// Parses an address string into its scheme and the canonical no-dash
    /// 32-hex Guid path. The returned <c>Path</c> is always rendered via
    /// <see cref="GuidFormatter.Format"/> so downstream callers (the CLI's
    /// <see cref="ApiClient"/>, the API's <c>AddressDto</c> consumers) see
    /// a single canonical wire form regardless of which lenient input
    /// shape the operator typed.
    /// </summary>
    /// <exception cref="FormatException">
    /// Thrown when <paramref name="address"/> does not match the
    /// <c>scheme:&lt;guid&gt;</c> shape, or when the Guid component cannot
    /// be parsed.
    /// </exception>
    public static (string Scheme, string Path) Parse(string address)
    {
        if (string.IsNullOrEmpty(address))
        {
            throw new FormatException(
                "Invalid address format: address is empty. Expected 'scheme:<guid>'.");
        }

        var separatorIndex = address.IndexOf(':');
        if (separatorIndex <= 0 || separatorIndex == address.Length - 1)
        {
            throw new FormatException(
                $"Invalid address format: '{address}'. Expected 'scheme:<guid>' (e.g. 'agent:8c5fab2a8e7e4b9c92f1d8a3b4c5d6e7').");
        }

        var scheme = address[..separatorIndex];
        var idPart = address[(separatorIndex + 1)..];

        if (!GuidFormatter.TryParse(idPart, out var id))
        {
            throw new FormatException(
                $"Invalid address format: '{address}'. The id component must be a Guid (32-hex no-dash or dashed form).");
        }

        return (scheme, GuidFormatter.Format(id));
    }
}
