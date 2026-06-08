// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Auth;

using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Core.Security;
using Cvoya.Spring.Dapr.Data;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

/// <summary>
/// Default scoped implementation of <see cref="IParticipantDisplayNameResolver"/>.
/// Resolves a wire-form participant address (post-#1629:
/// <c>scheme:&lt;32-hex-no-dash&gt;</c>) into a human-readable display
/// name by joining onto the appropriate definition / humans / connector
/// table.
///
/// <para>
/// Lives in <c>Cvoya.Spring.Dapr</c> because the implementation depends
/// on <see cref="SpringDbContext"/>; the interface
/// (<see cref="IParticipantDisplayNameResolver"/>) lives in
/// <c>Cvoya.Spring.Core</c> so both the API host (portal-side
/// participant projection) and the in-process prompt-assembly path
/// (#2129) can depend on it without coupling to the host or the data
/// layer.
/// </para>
///
/// <para>
/// Schemes covered: <c>agent</c>, <c>unit</c>, <c>human</c>,
/// <c>connector</c>, <c>tenant-user</c>. Unknown schemes flow into the
/// generic fallback path rather than leaking a raw identifier.
/// </para>
///
/// <para>
/// <b>The parsed scheme selects the table (#3134 / #2084).</b> The scheme is
/// read from a wire-form address the platform itself stamped, so
/// <c>scheme:id</c> is an authoritative identity, not the claimed-scheme /
/// trial-and-error anti-pattern the #2084 seam targets. The per-scheme switch
/// below therefore trusts the scheme to pick the table, and a
/// <c>scheme:id</c> whose id is absent from that table resolves to the
/// per-scheme generic fallback — the read-side analogue of the "id is not of
/// that scheme ⇒ not found" rule the membership write path enforces. A
/// genuine scheme/id mismatch here would be data corruption, not caller input.
/// </para>
///
/// <para>
/// This is deliberately NOT routed through
/// <see cref="Cvoya.Spring.Core.Directory.IDirectoryService.ResolveKindAsync"/>:
/// that seam reads the process-wide <c>DirectoryCache</c>, which is NOT
/// tenant-partitioned, so a cache hit could surface another tenant's name on
/// this tenant-scoped read path; it covers only <c>agent</c>/<c>unit</c> (not
/// <c>connector</c>/<c>tenant-user</c>/<c>human</c> or the legacy slug forms);
/// and it cannot express this resolver's non-empty/never-throw contract
/// (below). The direct <c>AsNoTracking</c> reads here are tenant-safe and
/// always-DB-fresh.
/// </para>
///
/// <para>
/// <b>Non-empty contract (#1635 / #2532 / #2533).</b> The resolver
/// always returns a non-empty string. Resolution failures fall through
/// tiers:
/// </para>
/// <list type="number">
///   <item><description>
///     The directory / definition row is found and has a non-empty
///     display name → return it.
///   </description></item>
///   <item><description>
///     A connector row is found but has no display name → return
///     <c>"a {Type} connector"</c> (e.g. <c>"a github connector"</c>)
///     so operators still see the connector kind.
///   </description></item>
///   <item><description>
///     The row is missing (entity was deleted or never existed) → return
///     the per-scheme generic fallback (<c>"an agent"</c>, <c>"a unit"</c>,
///     <c>"a connector"</c>, <c>"someone"</c>, <c>"a member"</c>) marked
///     <c>IsFallback = true</c>. Snapshot-aware callers (the thread
///     endpoint) substitute a previously-captured real name in this case.
///     Unknown schemes return <c>"a {scheme}"</c> — strictly better than
///     leaking a GUID.
///   </description></item>
///   <item><description>
///     The address itself is malformed / empty → return the address
///     verbatim so logs / debugging surfaces still carry the raw value.
///     Marked <c>IsFallback = true</c> because there is no entity to
///     describe.
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
    private readonly Dictionary<string, ParticipantDisplayName> _cache = new(StringComparer.Ordinal);
    private readonly Dictionary<string, bool> _deletedCache = new(StringComparer.Ordinal);

    /// <inheritdoc />
    public async ValueTask<string> ResolveAsync(
        string address,
        CancellationToken cancellationToken = default)
    {
        var status = await ResolveStatusAsync(address, cancellationToken);
        return status.DisplayName;
    }

    /// <inheritdoc />
    public async ValueTask<ParticipantDisplayName> ResolveStatusAsync(
        string address,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(address))
        {
            // Truly empty input — there is no scheme to fall back on, so
            // surface the unknown-scheme generic so callers still receive
            // a non-empty string per the type-level contract.
            return new ParticipantDisplayName(GenericFallback(scheme: null), IsFallback: true);
        }

        if (string.IsNullOrWhiteSpace(address))
        {
            // Whitespace-only — preserve the input verbatim for log
            // grep-ability; mark as fallback because there is no entity
            // behind it.
            return new ParticipantDisplayName(address, IsFallback: true);
        }

        if (_cache.TryGetValue(address, out var cached))
        {
            return cached;
        }

        var result = await ResolveInternalAsync(address, cancellationToken);
        _cache[address] = result;
        return result;
    }

    /// <inheritdoc />
    public async ValueTask<bool> IsDeletedAsync(
        string address,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(address))
        {
            // Empty / whitespace — cannot reason about a missing entity,
            // so the orphan check defaults to "not deleted" per the
            // conservative-fallback contract on the interface.
            return false;
        }

        if (_deletedCache.TryGetValue(address, out var cached))
        {
            return cached;
        }

        var result = await IsDeletedInternalAsync(address, cancellationToken);
        _deletedCache[address] = result;
        return result;
    }

    private async Task<bool> IsDeletedInternalAsync(
        string address,
        CancellationToken cancellationToken)
    {
        var (scheme, idText) = ParseAddress(address);
        if (scheme is null || idText is null)
        {
            return false;
        }

        if (!Cvoya.Spring.Core.Identifiers.GuidFormatter.TryParse(idText, out var idGuid))
        {
            // Slug-form legacy addresses carry no Guid; we cannot look
            // them up in the definition tables. Treat as not-deleted so
            // the orphan check does not blank legacy threads from the
            // live list.
            return false;
        }

        // Humans are not archivable: the user is always live on their
        // own engagements. Short-circuit ahead of the DB lookup.
        if (string.Equals(scheme, Address.HumanScheme, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // Per-scheme deleted-status lookups. The DbContext's tenant +
        // soft-delete query filters DROP soft-deleted rows by default,
        // so "row not found through the default query" subsumes both
        // "missing entirely" and "soft-deleted in this tenant" — exactly
        // the spec on IsDeletedAsync. We deliberately do NOT call
        // IgnoreQueryFilters here so a cross-tenant row that exists in
        // another tenant still surfaces as deleted-or-missing from the
        // perspective of the caller's tenant.
        try
        {
            if (string.Equals(scheme, Address.AgentScheme, StringComparison.OrdinalIgnoreCase))
            {
                var exists = await db.AgentDefinitions
                    .AsNoTracking()
                    .AnyAsync(a => a.Id == idGuid, cancellationToken);
                return !exists;
            }

            if (string.Equals(scheme, Address.UnitScheme, StringComparison.OrdinalIgnoreCase))
            {
                var exists = await db.UnitDefinitions
                    .AsNoTracking()
                    .AnyAsync(u => u.Id == idGuid, cancellationToken);
                return !exists;
            }

            if (string.Equals(scheme, Address.ConnectorScheme, StringComparison.OrdinalIgnoreCase))
            {
                var exists = await db.ConnectorDefinitions
                    .AsNoTracking()
                    .AnyAsync(c => c.Id == idGuid, cancellationToken);
                return !exists;
            }

            if (string.Equals(scheme, Address.TenantUserScheme, StringComparison.OrdinalIgnoreCase))
            {
                // TenantUsers carries no DeletedAt column today — the row
                // either exists (live) or it doesn't (deleted).
                var exists = await db.TenantUsers
                    .AsNoTracking()
                    .AnyAsync(u => u.Id == idGuid, cancellationToken);
                return !exists;
            }
        }
        catch (Exception ex)
        {
            logger.LogDebug(
                ex,
                "Failed to read deleted-status for address {Address}; treating as not-deleted.",
                address);
            return false;
        }

        // Unknown scheme — conservative fallback.
        return false;
    }

    private async Task<ParticipantDisplayName> ResolveInternalAsync(
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
            // carry the value. Flag as fallback so snapshot-aware
            // callers can prefer a captured name if they have one.
            return new ParticipantDisplayName(address, IsFallback: true);
        }

        if (!Cvoya.Spring.Core.Identifiers.GuidFormatter.TryParse(idText, out var idGuid))
        {
            // Slug-shaped legacy address (e.g. "human://savas"). The slug
            // IS the human-readable label, so return it verbatim — not a
            // fallback.
            return string.IsNullOrEmpty(idText)
                ? new ParticipantDisplayName(GenericFallback(scheme), IsFallback: true)
                : new ParticipantDisplayName(idText, IsFallback: false);
        }

        if (string.Equals(scheme, Address.HumanScheme, StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var name = await humanIdentityResolver.GetDisplayNameAsync(idGuid, cancellationToken);
                if (!string.IsNullOrWhiteSpace(name))
                {
                    return new ParticipantDisplayName(name, IsFallback: false);
                }
            }
            catch (Exception ex)
            {
                logger.LogDebug(
                    ex,
                    "Failed to resolve display name for human {HumanId}; using fallback.",
                    idGuid);
            }

            return new ParticipantDisplayName(GenericFallback(scheme), IsFallback: true);
        }

        if (string.Equals(scheme, Address.AgentScheme, StringComparison.OrdinalIgnoreCase))
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
                    return new ParticipantDisplayName(name, IsFallback: false);
                }

                logger.LogDebug(
                    "No agent definition found for actor id {ActorId}; using fallback.",
                    idGuid);
            }
            catch (Exception ex)
            {
                logger.LogDebug(
                    ex,
                    "Failed to resolve display name for agent actor id {ActorId}; using fallback.",
                    idGuid);
            }

            return new ParticipantDisplayName(GenericFallback(scheme), IsFallback: true);
        }

        if (string.Equals(scheme, Address.UnitScheme, StringComparison.OrdinalIgnoreCase))
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
                    return new ParticipantDisplayName(name, IsFallback: false);
                }

                logger.LogDebug(
                    "No unit definition found for actor id {ActorId}; using fallback.",
                    idGuid);
            }
            catch (Exception ex)
            {
                logger.LogDebug(
                    ex,
                    "Failed to resolve display name for unit actor id {ActorId}; using fallback.",
                    idGuid);
            }

            return new ParticipantDisplayName(GenericFallback(scheme), IsFallback: true);
        }

        // ADR-0047 §1: the TenantUser actor kind. Reads
        // tenant_users.display_name keyed on the address Guid; the tenant
        // query filter on the DbContext scopes the read to the active
        // tenant per CONVENTIONS § 12, so a cross-tenant id surfaces as
        // the per-scheme fallback rather than leaking the row's existence.
        if (string.Equals(scheme, Address.TenantUserScheme, StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var name = await db.TenantUsers
                    .AsNoTracking()
                    .Where(u => u.Id == idGuid)
                    .Select(u => u.DisplayName)
                    .FirstOrDefaultAsync(cancellationToken);

                if (!string.IsNullOrWhiteSpace(name))
                {
                    return new ParticipantDisplayName(name, IsFallback: false);
                }

                logger.LogDebug(
                    "No tenant_user found for actor id {ActorId}; using fallback.",
                    idGuid);
            }
            catch (Exception ex)
            {
                logger.LogDebug(
                    ex,
                    "Failed to resolve display name for tenant_user actor id {ActorId}; using fallback.",
                    idGuid);
            }

            return new ParticipantDisplayName(GenericFallback(scheme), IsFallback: true);
        }

        // #2532: connectors leaked their hex id when no case matched. The
        // catalog slug (e.g. "github") makes the fallback strictly more
        // useful than the generic "a connector" — "a github connector"
        // tells the operator what bridged the message in.
        if (string.Equals(scheme, Address.ConnectorScheme, StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var row = await db.ConnectorDefinitions
                    .AsNoTracking()
                    .Where(c => c.Id == idGuid && c.DeletedAt == null)
                    .Select(c => new { c.DisplayName, c.Type })
                    .FirstOrDefaultAsync(cancellationToken);

                if (row is not null)
                {
                    if (!string.IsNullOrWhiteSpace(row.DisplayName))
                    {
                        return new ParticipantDisplayName(row.DisplayName, IsFallback: false);
                    }

                    if (!string.IsNullOrWhiteSpace(row.Type))
                    {
                        // Kind-aware fallback. Not a real display name, so
                        // mark fallback=true — a snapshot of the connector's
                        // pre-rename / pre-delete display name still wins.
                        return new ParticipantDisplayName(
                            $"a {row.Type.ToLowerInvariant()} connector",
                            IsFallback: true);
                    }
                }

                logger.LogDebug(
                    "No connector definition found for actor id {ActorId}; using fallback.",
                    idGuid);
            }
            catch (Exception ex)
            {
                logger.LogDebug(
                    ex,
                    "Failed to resolve display name for connector actor id {ActorId}; using fallback.",
                    idGuid);
            }

            return new ParticipantDisplayName(GenericFallback(scheme), IsFallback: true);
        }

        // Unknown scheme — surface the per-scheme generic ("a {scheme}")
        // rather than the raw id. The previous "return idText;" branch
        // was the source of the GUID-as-display-name bug in #2532.
        return new ParticipantDisplayName(GenericFallback(scheme), IsFallback: true);
    }

    /// <summary>
    /// Per-scheme generic display label used when the directory cannot
    /// resolve a real name. The mapping is intentionally conversational:
    /// the portal renders the label inline ("…with an agent…") and
    /// "Unit 'X' invited <i>an agent</i>" reads better than the previous
    /// <c>&lt;deleted&gt;</c> sentinel. Unknown schemes get
    /// <c>"a {scheme}"</c> — strictly better than the raw GUID.
    /// </summary>
    private static string GenericFallback(string? scheme)
    {
        if (string.IsNullOrWhiteSpace(scheme))
        {
            return "an unknown participant";
        }

        var normalised = scheme.Trim().ToLowerInvariant();
        return normalised switch
        {
            Address.HumanScheme => "someone",
            Address.AgentScheme => "an agent",
            Address.UnitScheme => "a unit",
            Address.ConnectorScheme => "a connector",
            Address.TenantUserScheme => "a member",
            _ => $"a {normalised}",
        };
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
