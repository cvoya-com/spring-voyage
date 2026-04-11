// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Cli;

/// <summary>
/// Parses address strings in the format "scheme://path" (e.g. "agent://ada").
/// </summary>
public static class AddressParser
{
    /// <summary>
    /// Parses an address string into its scheme and path components.
    /// </summary>
    /// <exception cref="FormatException">Thrown when the address format is invalid.</exception>
    public static (string Scheme, string Path) Parse(string address)
    {
        var separatorIndex = address.IndexOf("://", StringComparison.Ordinal);
        if (separatorIndex < 0)
        {
            throw new FormatException($"Invalid address format: '{address}'. Expected format: scheme://path");
        }

        var scheme = address[..separatorIndex];
        var path = address[(separatorIndex + 3)..];

        if (string.IsNullOrEmpty(scheme) || string.IsNullOrEmpty(path))
        {
            throw new FormatException($"Invalid address format: '{address}'. Scheme and path must not be empty.");
        }

        return (scheme, path);
    }
}