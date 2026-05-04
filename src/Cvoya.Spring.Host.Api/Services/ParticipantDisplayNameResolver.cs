// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Api.Services;

using Cvoya.Spring.Core.Security;
using Cvoya.Spring.Dapr.Data;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

/// <summary>
/// Default scoped implementation of <see cref="IParticipantDisplayNameResolver"/>.
/// Resolves a wire-form participant address (post-#1629:
/// <c>scheme:&lt;32-hex-no-dash&gt;</c>) into a human-readable display
/// name by joining onto the appropriate definition / humans table.
///
/// <para>
/// Schemes covered:
/// <list type="bullet">
///   <item><description>
///     <c>agent:&lt;guid&gt;</c> → <c>AgentDefinitions.DisplayName</c>.
///   </description></item>
///   <item><description>
///     <c>unit:&lt;guid&gt;</c> → <c>UnitDefinitions.DisplayName</c>.
///   </description></item>
///   <item><description>
///     <c>human:&lt;guid&gt;</c> → resolved via
///     <see cref="IHumanIdentityResolver.GetDisplayNameAsync"/> (which
///     reads <c>humans.display_name</c>, falling back to <c>username</c>).
///   </description></item>
/// </list>
/// </para>
///
/// <para>
/// <b>Non-empty contract (#1635).</b> The resolver always returns a
/// non-empty string. Resolution failures fall through tiers:
/// </para>
/// <list type="number">
///   <item><description>
///     The directory / definition row is found and has a non-empty
///     display name → return it.
///   </description></item>
///   <item><description>
///     The row is missing (entity was deleted or never existed) → return
///     the literal <c>&lt;deleted&gt;</c> placeholder so the portal can
///     render a friendly tag without leaking GUIDs.
///   </description></item>
///   <item><description>
///     The address itself is malformed / empty → return the address
///     verbatim so logs / debugging surfaces still carry the raw value.
///   </description></item>
/// </list>
///
/// <para>
/// Results are cached in a per-request dictionary so repeated calls for
/// the same address (e.g. a single human appearing as the <c>Human</c>
/// on multiple inbox rows) issue at most one database round-trip.
/// </para>
/// </summary>
internal sealed class ParticipantDisplayNameResolver(
    SpringDbContext db,
    IHumanIdentityResolver humanIdentityResolver,
    ILogger<ParticipantDisplayNameResolver> logger)
    : IParticipantDisplayNameResolver
{
    /// <summary>
    /// Sentinel display name returned for participant addresses whose
    /// backing entity (agent / unit / human) is no longer present in the
    /// directory / humans table. Surfaces as a friendly tag in the
    /// portal — see #1630, #1635.
    /// </summary>
    public const string DeletedDisplayName = "<deleted>";

    private readonly Dictionary<string, string> _cache = new(StringComparer.Ordinal);

    /// <inheritdoc />
    public async ValueTask<string> ResolveAsync(
        string address,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(address))
        {
            return string.IsNullOrEmpty(address) ? DeletedDisplayName : address;
        }

        if (_cache.TryGetValue(address, out var cached))
        {
            return cached;
        }

        var result = await ResolveInternalAsync(address, cancellationToken);
        _cache[address] = result;
        return result;
    }

    private async Task<string> ResolveInternalAsync(
        string address,
        CancellationToken cancellationToken)
    {
        // Post-#1629 canonical wire form: "scheme:<32-hex-no-dash>".
        // Legacy forms ("scheme://path", "scheme:id:<uuid>") are still
        // accepted defensively — older activity-event sources predate the
        // baseline migration and the resolver is read-side; failing them
        // would blank participant names on legacy threads.
        var (scheme, idText) = ParseAddress(address);
        if (scheme is null || idText is null)
        {
            // Truly malformed — return the raw address so logs still
            // carry the value.
            return address;
        }

        if (!Cvoya.Spring.Core.Identifiers.GuidFormatter.TryParse(idText, out var idGuid))
        {
            // Slug-shaped legacy address (e.g. "human://savas"). The slug
            // IS the human-readable label, so return it verbatim.
            return string.IsNullOrEmpty(idText) ? DeletedDisplayName : idText;
        }

        if (string.Equals(scheme, "human", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var name = await humanIdentityResolver.GetDisplayNameAsync(idGuid, cancellationToken);
                if (!string.IsNullOrWhiteSpace(name))
                {
                    return name;
                }
            }
            catch (Exception ex)
            {
                logger.LogDebug(
                    ex,
                    "Failed to resolve display name for human {HumanId}; treating as deleted.",
                    idGuid);
            }

            return DeletedDisplayName;
        }

        if (string.Equals(scheme, "agent", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var name = await db.AgentDefinitions
                    .AsNoTracking()
                    .Where(a => a.Id == idGuid && a.DeletedAt == null)
                    .Select(a => a.DisplayName)
                    .FirstOrDefaultAsync(cancellationToken);

                if (!string.IsNullOrWhiteSpace(name))
                {
                    return name;
                }

                logger.LogDebug(
                    "No agent definition found for actor id {ActorId}; treating as deleted.",
                    idGuid);
            }
            catch (Exception ex)
            {
                logger.LogDebug(
                    ex,
                    "Failed to resolve display name for agent actor id {ActorId}; treating as deleted.",
                    idGuid);
            }

            return DeletedDisplayName;
        }

        if (string.Equals(scheme, "unit", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var name = await db.UnitDefinitions
                    .AsNoTracking()
                    .Where(u => u.Id == idGuid && u.DeletedAt == null)
                    .Select(u => u.DisplayName)
                    .FirstOrDefaultAsync(cancellationToken);

                if (!string.IsNullOrWhiteSpace(name))
                {
                    return name;
                }

                logger.LogDebug(
                    "No unit definition found for actor id {ActorId}; treating as deleted.",
                    idGuid);
            }
            catch (Exception ex)
            {
                logger.LogDebug(
                    ex,
                    "Failed to resolve display name for unit actor id {ActorId}; treating as deleted.",
                    idGuid);
            }

            return DeletedDisplayName;
        }

        // Unknown scheme — return the id as-is rather than the deleted
        // sentinel so operator tooling can still trace it.
        return idText;
    }

    /// <summary>
    /// Splits a wire-form participant address into its <c>(scheme, id)</c>
    /// components. Accepts the canonical post-#1629 form
    /// (<c>scheme:&lt;hex&gt;</c>) AND the two legacy forms produced by
    /// pre-#1629 activity events:
    /// <list type="bullet">
    ///   <item><description><c>scheme://path</c> — slug-form navigation.</description></item>
    ///   <item><description><c>scheme:id:&lt;uuid&gt;</c> — explicit identity-form.</description></item>
    /// </list>
    /// Returns <c>(null, null)</c> for malformed input.
    /// </summary>
    private static (string? scheme, string? id) ParseAddress(string address)
    {
        // Identity form: "scheme:id:<uuid>" — recognise the explicit
        // ":id:" infix first to avoid colliding with the canonical form.
        var idIdx = address.IndexOf(":id:", StringComparison.Ordinal);
        if (idIdx > 0)
        {
            return (address[..idIdx], address[(idIdx + 4)..]);
        }

        // Navigation form: "scheme://path".
        var navIdx = address.IndexOf("://", StringComparison.Ordinal);
        if (navIdx > 0)
        {
            return (address[..navIdx], address[(navIdx + 3)..]);
        }

        // Canonical form: "scheme:<hex>" — single ':' separator.
        var colonIdx = address.IndexOf(':');
        if (colonIdx > 0 && colonIdx < address.Length - 1)
        {
            return (address[..colonIdx], address[(colonIdx + 1)..]);
        }

        return (null, null);
    }
}