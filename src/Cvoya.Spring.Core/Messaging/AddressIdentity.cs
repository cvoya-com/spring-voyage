// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Messaging;

using Cvoya.Spring.Core.Identifiers;

/// <summary>
/// Tolerant address-to-identity extractor for read-side surfaces that meet
/// addresses persisted under more than one rendering convention.
/// </summary>
/// <remarks>
/// <para>
/// Issue #2082: identity comparisons of senders / recipients / participants
/// must be done on the typed <see cref="Guid"/> identity of the actor, not
/// on the address string. Addresses are a presentation concept and may
/// legitimately surface in more than one shape across the read surface —
/// canonical <c>scheme:&lt;hex&gt;</c> (post-#1629), navigation-form
/// <c>scheme://path</c> (legacy threads), and identity-form
/// <c>scheme:id:&lt;hex&gt;</c> (legacy activity events). This helper
/// extracts the Guid identity from any of those forms so callers can
/// compare equality on the stable primitive.
/// </para>
/// <para>
/// Callers who need to compare equality must use <see cref="TryGetActorId"/>
/// — never raw string equality. Routing, display, and structural parsing
/// (the kind <see cref="Address.TryParse"/> handles for the canonical form)
/// stay in their existing helpers.
/// </para>
/// </remarks>
public static class AddressIdentity
{
    /// <summary>
    /// Attempts to extract the actor's stable <see cref="Guid"/> identity
    /// from a wire-form address string. Accepts the canonical
    /// <c>scheme:&lt;32-hex-no-dash&gt;</c> form, the navigation-form
    /// <c>scheme://&lt;hex&gt;</c>, and the legacy identity-form
    /// <c>scheme:id:&lt;hex&gt;</c>. Dashed Guids are tolerated
    /// (<see cref="GuidFormatter.TryParse"/> is lenient) so copy-paste
    /// flows continue to work.
    /// </summary>
    /// <param name="address">The address-like string to parse.</param>
    /// <param name="id">The extracted Guid identity when parsing succeeds.</param>
    /// <returns>
    /// <c>true</c> when an actor Guid was extracted; <c>false</c> when the
    /// string is empty, malformed, or carries a slug-shaped legacy id that
    /// is not a Guid (in which case there is no typed identity to compare).
    /// </returns>
    public static bool TryGetActorId(string? address, out Guid id)
    {
        id = Guid.Empty;
        if (string.IsNullOrEmpty(address))
        {
            return false;
        }

        // Identity form: "scheme:id:<hex>" — check the explicit ":id:"
        // infix first to avoid colliding with the canonical form.
        var idIdx = address.IndexOf(":id:", StringComparison.Ordinal);
        if (idIdx > 0)
        {
            return GuidFormatter.TryParse(address[(idIdx + 4)..], out id);
        }

        // Navigation form: "scheme://path".
        var navIdx = address.IndexOf("://", StringComparison.Ordinal);
        if (navIdx > 0)
        {
            return GuidFormatter.TryParse(address[(navIdx + 3)..], out id);
        }

        // Canonical form: "scheme:<hex>" — single ':' separator.
        var colonIdx = address.IndexOf(':');
        if (colonIdx > 0 && colonIdx < address.Length - 1)
        {
            return GuidFormatter.TryParse(address[(colonIdx + 1)..], out id);
        }

        return false;
    }
}
