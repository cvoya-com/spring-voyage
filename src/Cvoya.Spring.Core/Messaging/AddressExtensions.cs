// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Messaging;

using Cvoya.Spring.Core.Identifiers;

/// <summary>
/// Convenience extensions over <see cref="Address"/> for callers that
/// need the Guid identity rendered as a string (Dapr <c>ActorId</c>
/// keying, log lines, dictionary keys).
/// </summary>
public static class AddressExtensions
{
    /// <summary>
    /// Returns the canonical no-dash 32-char hex form of
    /// <see cref="Address.Id"/> — matches the wire form emitted by
    /// <see cref="Address.ToString"/> minus the scheme prefix. Use this
    /// anywhere a string Guid is needed (Dapr actor keying, log
    /// correlation, dictionary keys).
    /// </summary>
    public static string IdString(this Address address) =>
        GuidFormatter.Format(address.Id);
}
