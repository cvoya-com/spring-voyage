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
    /// Canonical scheme for connector-shaped addresses. Per ADR-0048 a
    /// connector is a non-routable bridge: the scheme survives only as
    /// message provenance — the synthetic <c>connector://…</c>
    /// <see cref="Message.From"/> stamped on a translated inbound webhook
    /// event. Nothing routes <em>to</em> a connector address. The platform
    /// host keys connector-origin handling (e.g. the routing-decision
    /// activity emitted after a connector event is processed, issue #2560)
    /// off this scheme.
    /// </summary>
    public const string ConnectorScheme = "connector";

    /// <summary>
    /// Canonical scheme for tenant-user-shaped addresses (ADR-0047 §1) —
    /// the authenticated principal of Spring Voyage scoped to one tenant.
    /// Pinned here as part of the actor-kind audit ADR-0047 § "Costs"
    /// flagged; Phase F (OAuth wiring) is the first runtime consumer.
    /// </summary>
    public const string TenantUserScheme = "tenant-user";

    /// <summary>
    /// True when this address names a participant the platform can deliver
    /// to — an <see cref="AgentScheme"/>, <see cref="UnitScheme"/>, or
    /// <see cref="HumanScheme"/> address. Connector origins
    /// (<see cref="ConnectorScheme"/>, provenance-only per ADR-0048/0053)
    /// and the auth principal (<see cref="TenantUserScheme"/>, resolved to a
    /// <c>human://</c> Hat before routing per ADR-0047/0062) are
    /// non-routable. Used to derive the envelope's routable
    /// <c>participants</c> roster (ADR-0064) and to gate messaging-tool
    /// recipients to addressable kinds.
    /// </summary>
    public bool IsRoutable =>
        string.Equals(Scheme, AgentScheme, StringComparison.OrdinalIgnoreCase)
        || string.Equals(Scheme, UnitScheme, StringComparison.OrdinalIgnoreCase)
        || string.Equals(Scheme, HumanScheme, StringComparison.OrdinalIgnoreCase);

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
            throw new InvalidAddressIdException(scheme, idString);
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
    /// the canonical no-dash form (<c>scheme:8c5fab…</c>), the dashed
    /// form (<c>scheme:8c5fab2a-8e7e-…</c>) — <see cref="Guid.TryParse"/>
    /// is lenient — and a URI-style authority marker
    /// (<c>scheme://8c5fab…</c>) which the portal and CLI runtimes emit.
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

        // Lenient: tolerate a URI-style "//" authority marker after the
        // scheme colon. The canonical wire form is scheme:<id>, but the
        // platform's own portal and CLI runtimes routinely emit
        // scheme://<id>; accept both so a routing call is not rejected on
        // a cosmetic prefix.
        if (idPart.StartsWith("//", StringComparison.Ordinal))
        {
            idPart = idPart[2..];
        }

        if (!GuidFormatter.TryParse(idPart, out var id))
        {
            return false;
        }

        address = new Address(scheme, id);
        return true;
    }
}

/// <summary>
/// Thrown by <see cref="Address.For(string, string)"/> when the id segment
/// cannot be parsed as a Guid. Subtype of <see cref="ArgumentException"/>
/// so existing catches still work; the dedicated type lets the HTTP host
/// translate this specific failure into a 400 ProblemDetails instead of
/// the framework default 500 (#2250).
/// </summary>
public sealed class InvalidAddressIdException : ArgumentException
{
    public InvalidAddressIdException(string scheme, string idString)
        : base($"Address id '{idString}' is not a valid Guid (scheme '{scheme}').", nameof(idString))
    {
        Scheme = scheme;
        IdString = idString;
    }

    /// <summary>The address scheme passed to <see cref="Address.For"/>.</summary>
    public string Scheme { get; }

    /// <summary>The raw id string that failed Guid parsing.</summary>
    public string IdString { get; }
}
