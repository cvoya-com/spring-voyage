// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Messaging;

using System.Runtime.Serialization;

/// <summary>
/// Represents an addressable endpoint in the Spring Voyage platform.
/// The scheme identifies the type of addressable (e.g., "agent", "unit", "connector")
/// and the path identifies the specific instance (e.g., "engineering-team/ada").
/// </summary>
/// <remarks>
/// <para>
/// Two wire forms exist for agent and unit addresses:
/// <list type="bullet">
///   <item><description>
///     <b>Navigation form</b> (<c>scheme://path</c>) — slug-based, used for discovery.
///     Constructed via <see cref="ForAgent"/> / <see cref="ForUnit"/>.
///   </description></item>
///   <item><description>
///     <b>Identity form</b> (<c>scheme:id:&lt;uuid&gt;</c>) — UUID-based stable identity.
///     Constructed via <see cref="ForIdentity(string, Guid)"/>; emitted via
///     <see cref="ToIdentityUri()"/>. This form is unambiguous even when a slug
///     looks like a UUID string.
///   </description></item>
/// </list>
/// </para>
/// <para>
/// Travels across the Dapr Actor remoting boundary as the argument to
/// <c>IUnitActor.AddMemberAsync</c> / <c>RemoveMemberAsync</c>; the
/// <c>[DataContract]</c> annotations let <c>DataContractSerializer</c> handle
/// positional records that lack a parameterless constructor (#261).
/// </para>
/// </remarks>
/// <param name="Scheme">The address scheme identifying the type of addressable.</param>
/// <param name="Path">The path identifying the specific addressable instance.</param>
/// <param name="IsIdentity">
/// <c>true</c> when the address is in the identity form (<c>scheme:id:&lt;uuid&gt;</c>);
/// <c>false</c> (default) for the navigation form (<c>scheme://path</c>).
/// </param>
[DataContract]
public record Address(
    [property: DataMember] string Scheme,
    [property: DataMember] string Path,
    [property: DataMember] bool IsIdentity = false)
{
    /// <summary>Canonical scheme for agent-shaped addresses.</summary>
    public const string AgentScheme = "agent";

    /// <summary>Canonical scheme for unit-shaped addresses.</summary>
    public const string UnitScheme = "unit";

    /// <summary>
    /// The separator segment in the identity URI form
    /// (<c>scheme:id:&lt;uuid&gt;</c>).
    /// </summary>
    private const string IdentitySegment = "id:";

    /// <summary>
    /// Returns the compact string form. For navigation addresses this is
    /// <c>scheme:path</c>; for identity addresses this is
    /// <c>scheme:id:&lt;uuid&gt;</c>.
    /// </summary>
    public sealed override string ToString() =>
        IsIdentity ? $"{Scheme}:{IdentitySegment}{Path}" : $"{Scheme}:{Path}";

    /// <summary>
    /// Returns the canonical URI form used by wire-shape projections.
    /// <list type="bullet">
    ///   <item><description>
    ///     Navigation form: <c>scheme://path</c> — used for log lines, the
    ///     <c>member</c> field, and activity source columns.
    ///   </description></item>
    ///   <item><description>
    ///     Identity form: delegates to <see cref="ToIdentityUri()"/> and
    ///     returns <c>scheme:id:&lt;uuid&gt;</c>.
    ///   </description></item>
    /// </list>
    /// </summary>
    /// <remarks>
    /// Distinct from <see cref="ToString"/>, which uses a plain <c>:</c>
    /// separator for log lines / error messages.
    /// </remarks>
    public string ToCanonicalUri() =>
        IsIdentity ? ToIdentityUri() : $"{Scheme}://{Path}";

    /// <summary>
    /// Returns the stable identity URI form: <c>scheme:id:&lt;uuid&gt;</c>.
    /// Valid only when <see cref="IsIdentity"/> is <c>true</c>.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// Thrown when called on a navigation-form address.
    /// </exception>
    public string ToIdentityUri()
    {
        if (!IsIdentity)
        {
            throw new InvalidOperationException(
                $"ToIdentityUri() called on a navigation-form address '{Scheme}://{Path}'. " +
                "Use Address.ForIdentity(scheme, id) to construct an identity-form address.");
        }

        return $"{Scheme}:{IdentitySegment}{Path}";
    }

    /// <summary>Builds an agent-scheme navigation address (<c>agent://path</c>).</summary>
    public static Address ForAgent(string path) => new(AgentScheme, path);

    /// <summary>Builds a unit-scheme navigation address (<c>unit://path</c>).</summary>
    public static Address ForUnit(string path) => new(UnitScheme, path);

    /// <summary>
    /// Builds an identity-form address (<c>scheme:id:&lt;uuid&gt;</c>).
    /// The resulting address is unambiguous: the <c>id:</c> segment tells
    /// every consumer this is a stable actor UUID, not a slug that happens
    /// to look like a UUID.
    /// </summary>
    /// <param name="scheme">The address scheme (e.g. "agent", "unit").</param>
    /// <param name="id">The stable UUID for this actor.</param>
    public static Address ForIdentity(string scheme, Guid id) =>
        new(scheme, id.ToString(), IsIdentity: true);

    /// <summary>
    /// Attempts to parse a string into an <see cref="Address"/>.
    /// Accepts both the navigation form (<c>scheme://path</c>) and the
    /// identity form (<c>scheme:id:&lt;uuid&gt;</c>).
    /// </summary>
    /// <param name="value">The string to parse.</param>
    /// <param name="address">
    /// When this method returns <c>true</c>, contains the parsed address.
    /// </param>
    /// <returns>
    /// <c>true</c> if the string is a valid address in either form;
    /// <c>false</c> otherwise.
    /// </returns>
    public static bool TryParse(string value, out Address? address)
    {
        address = null;

        if (string.IsNullOrEmpty(value))
        {
            return false;
        }

        // Try identity form first: "scheme:id:<uuid>"
        var idIdx = value.IndexOf(":id:", StringComparison.Ordinal);
        if (idIdx > 0)
        {
            var scheme = value[..idIdx];
            var uuidPart = value[(idIdx + 4)..];
            if (!string.IsNullOrEmpty(scheme) && Guid.TryParse(uuidPart, out var guid))
            {
                address = new Address(scheme, guid.ToString(), IsIdentity: true);
                return true;
            }

            return false;
        }

        // Try navigation form: "scheme://path"
        var sepIdx = value.IndexOf("://", StringComparison.Ordinal);
        if (sepIdx > 0)
        {
            var scheme = value[..sepIdx];
            var path = value[(sepIdx + 3)..];
            if (!string.IsNullOrEmpty(scheme) && !string.IsNullOrEmpty(path))
            {
                address = new Address(scheme, path, IsIdentity: false);
                return true;
            }
        }

        return false;
    }
}