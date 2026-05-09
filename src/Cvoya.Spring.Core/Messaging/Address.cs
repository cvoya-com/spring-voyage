// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Messaging;

using System.Runtime.Serialization;

using Cvoya.Spring.Core.Identifiers;

/// <summary>
/// Represents an addressable endpoint in the Spring Voyage platform.
/// The scheme identifies the type of addressable (e.g., "agent", "unit",
/// "human", "connector") and <see cref="Id"/> is the actor's stable Guid.
/// </summary>
/// <remarks>
/// <para>
/// <b>Wire form</b>: <c>scheme:&lt;32-hex-no-dash&gt;</c> — e.g.
/// <c>agent:8c5fab2a8e7e4b9c92f1d8a3b4c5d6e7</c>. There is no slug-form;
/// every address is identity. Lenient parsing accepts dashed Guids too
/// (so copy-paste workflows continue to work) but emit always uses the
/// canonical 32-character lowercase no-dash form.
/// </para>
/// <para>
/// Travels across the Dapr Actor remoting boundary; the
/// <c>[DataContract]</c> annotations let
/// <c>DataContractSerializer</c> handle positional records that lack a
/// parameterless constructor.
/// </para>
/// </remarks>
/// <param name="Scheme">The address scheme (e.g. <c>agent</c>, <c>unit</c>, <c>human</c>).</param>
/// <param name="Id">The stable Guid identity of the addressable.</param>
[DataContract]
public record Address(
    [property: DataMember] string Scheme,
    [property: DataMember] Guid Id)
{
    /// <summary>Canonical scheme for agent-shaped addresses.</summary>
    public const string AgentScheme = "agent";

    /// <summary>Canonical scheme for unit-shaped addresses.</summary>
    public const string UnitScheme = "unit";

    /// <summary>Canonical scheme for human-shaped addresses.</summary>
    public const string HumanScheme = "human";

    /// <summary>
    /// Convenience accessor returning the Guid identity rendered in the
    /// canonical no-dash 32-char hex form. Useful for callers that need
    /// a string actor key (Dapr <c>ActorId</c> construction, log
    /// correlation, dictionary keys). Equivalent to
    /// <c>GuidFormatter.Format(Id)</c>.
    /// </summary>
    public string Path => GuidFormatter.Format(Id);

    /// <summary>
    /// Returns the canonical wire form: <c>scheme:&lt;32-hex-no-dash&gt;</c>.
    /// </summary>
    public sealed override string ToString() => $"{Scheme}:{Path}";

    /// <summary>
    /// Returns the canonical wire form. Alias of <see cref="ToString"/>
    /// kept for call sites that previously distinguished a navigation /
    /// identity URI form.
    /// </summary>
    public string ToCanonicalUri() => ToString();

    /// <summary>
    /// Builds an <see cref="Address"/> from a scheme + Guid-shaped id
    /// string. Parsing is lenient (accepts both dashed and no-dash
    /// forms via <see cref="Guid.TryParse"/>); throws
    /// <see cref="ArgumentException"/> when <paramref name="idString"/>
    /// cannot be parsed.
    /// </summary>
    public static Address For(string scheme, string idString)
    {
        if (!GuidFormatter.TryParse(idString, out var id))
        {
            throw new ArgumentException(
                $"Address id '{idString}' is not a valid Guid.",
                nameof(idString));
        }

        return new Address(scheme, id);
    }

    /// <summary>
    /// Builds an <see cref="Address"/> from a scheme + Guid identity.
    /// Convenience alias kept for call sites that historically used
    /// this factory.
    /// </summary>
    public static Address ForIdentity(string scheme, Guid id) => new(scheme, id);

    /// <summary>
    /// Attempts to parse a string into an <see cref="Address"/>. Accepts
    /// the canonical no-dash form (<c>scheme:8c5fab…</c>) and the dashed
    /// form (<c>scheme:8c5fab2a-8e7e-…</c>) — <see cref="Guid.TryParse"/>
    /// is lenient.
    /// </summary>
    /// <param name="value">The string to parse.</param>
    /// <param name="address">When <c>true</c>, contains the parsed address.</param>
    /// <returns><c>true</c> if the string is a valid address; otherwise <c>false</c>.</returns>
    public static bool TryParse(string? value, out Address? address)
    {
        address = null;

        if (string.IsNullOrEmpty(value))
        {
            return false;
        }

        var sepIdx = value.IndexOf(':');
        if (sepIdx <= 0 || sepIdx == value.Length - 1)
        {
            return false;
        }

        var scheme = value[..sepIdx];
        var idPart = value[(sepIdx + 1)..];

        if (!GuidFormatter.TryParse(idPart, out var id))
        {
            return false;
        }

        address = new Address(scheme, id);
        return true;
    }
}
