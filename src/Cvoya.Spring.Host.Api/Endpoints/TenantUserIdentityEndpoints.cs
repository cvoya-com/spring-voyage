// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Api.Endpoints;

using Cvoya.Spring.Core.Security;
using Cvoya.Spring.Core.Units;
using Cvoya.Spring.Dapr.Data;
using Cvoya.Spring.Dapr.Data.Entities;
using Cvoya.Spring.Host.Api.Auth;
using Cvoya.Spring.Host.Api.Models;
using Cvoya.Spring.Host.Api.Services;

using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// REST surface for the <c>TenantUser</c> envelope and its connector
/// display-identity rows (ADR-0047 §2 + §14). Replaces the prior
/// <c>HumanIdentityEndpoints.cs</c>'s <c>/identities</c> subset; the
/// per-<c>Human</c> envelope routes stay where they are because they are
/// unit-membership concerns owned by ADR-0046.
/// </summary>
/// <remarks>
/// <para>
/// Routes:
/// <list type="bullet">
///   <item><description><c>GET    /api/v1/tenant/users/{tenantUserId}</c> — read envelope.</description></item>
///   <item><description><c>PATCH  /api/v1/tenant/users/{tenantUserId}</c> — update displayName / description.</description></item>
///   <item><description><c>POST   /api/v1/tenant/users/{tenantUserId}/identities</c> — upsert identity row.</description></item>
///   <item><description><c>GET    /api/v1/tenant/users/{tenantUserId}/identities</c> — list identities.</description></item>
///   <item><description><c>DELETE /api/v1/tenant/users/{tenantUserId}/identities?connectorId=&amp;username=</c> — remove identity row.</description></item>
/// </list>
/// </para>
/// <para>
/// All routes are gated to <see cref="RolePolicies.TenantUser"/> via the
/// group binding in <c>Program.cs</c>. The implicit policy for "who can
/// edit whose identities" today is "the caller can manage any tenant
/// user in the current tenant" — that matches the OSS Operator default
/// and pre-dates the per-Human admin role. The cloud overlay tightens
/// this once tenant-multi-user is real.
/// </para>
/// </remarks>
public static class TenantUserIdentityEndpoints
{
    /// <summary>
    /// Registers the <c>TenantUser</c> envelope and identity routes under
    /// <c>/api/v1/tenant/users</c>.
    /// </summary>
    public static RouteGroupBuilder MapTenantUserIdentityEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/tenant/users")
            .WithTags("TenantUsers");

        group.MapGet("/{tenantUserId:guid}", GetTenantUserAsync)
            .WithName("GetTenantUser")
            .WithSummary("Read a single tenant user's read-side envelope.")
            .WithDescription("Returns the canonical fields needed by the portal's user-identity page and the CLI's read verbs. Identity lists live on the dedicated /identities sub-resource and are NOT embedded here.")
            .Produces<TenantUserResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound);

        // ADR-0062 § 6 / #2827: lookup-by-auth-subject so the CLI can
        // resolve a `<tenant-user-ref>` typed as the operator's OAuth
        // subject (e.g. an email or provider sub) without first knowing
        // the TenantUser UUID. Backs `spring unit member add human --as
        // alice@example.com` and `spring package install --as-human
        // <decl>=alice@example.com` parity. Scoped to the current tenant
        // by the standard ITenantContext query filter on TenantUsers.
        group.MapGet("/", FindTenantUserByAuthSubjectAsync)
            .WithName("FindTenantUserByAuthSubject")
            .WithSummary("Find a tenant user by OAuth subject within the current tenant (ADR-0062 § 6).")
            .WithDescription("Returns the TenantUser whose auth_subject matches the supplied query parameter in the current tenant, or 404 when no row matches. The query parameter is required; an empty value surfaces as 400. Used by the CLI to resolve `<tenant-user-ref>` shapes typed as OAuth subjects (#2827).")
            .Produces<TenantUserResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapPatch("/{tenantUserId:guid}", UpdateTenantUserAsync)
            .WithName("UpdateTenantUser")
            .WithSummary("Update a tenant user's editable identity fields (display name, description).")
            .WithDescription("Omitted fields leave the existing value untouched (PATCH semantics). DisplayName is validated via DisplayNameProblems.ValidateOrProblem; description has no length limit. Returns the post-write TenantUserResponse.")
            .Produces<TenantUserResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapPost("/{tenantUserId:guid}/identities", UpsertIdentityAsync)
            .WithName("UpsertTenantUserConnectorIdentity")
            .WithSummary("Create or update a tenant-user ↔ connector display-identity mapping (ADR-0047 §2).")
            .Produces<TenantUserConnectorIdentityResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        group.MapGet("/{tenantUserId:guid}/identities", ListIdentitiesAsync)
            .WithName("ListTenantUserConnectorIdentities")
            .WithSummary("List every connector identity row mapped to this tenant user.")
            .Produces<IReadOnlyList<TenantUserConnectorIdentityResponse>>(StatusCodes.Status200OK);

        group.MapDelete("/{tenantUserId:guid}/identities", RemoveIdentityAsync)
            .WithName("RemoveTenantUserConnectorIdentity")
            .WithSummary("Remove a connector identity mapping by (tenantUser, connector, username). Idempotent — returns 204 even when nothing matched.")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status400BadRequest);

        // ADR-0062 § 2: pin the tenant user's default "speaking-as" Hat
        // for new outbound messages. The PATCH validates that the Human
        // is bound to the named TenantUser via the `humans.tenant_user_id`
        // FK; an unbound Human surfaces a CLI-friendly 400 so the operator
        // gets a clear error rather than a silent FK-violation 500.
        // Backs `spring user identity set-primary <human-ref>` (#2808).
        group.MapPatch("/{tenantUserId:guid}/primary-human", SetPrimaryHumanAsync)
            .WithName("SetPrimaryHuman")
            .WithSummary("Pin the tenant user's primary Human (the default 'speaking-as' Hat for new outbound messages).")
            .WithDescription("ADR-0062 § 2: writes `tenant_users.primary_human_id`. The supplied Human must be bound to the target TenantUser via `humans.tenant_user_id`; an unbound Human returns 400 with a CLI-friendly message. Returns the post-write (tenantUserId, primaryHumanId) tuple.")
            .Produces<SetPrimaryHumanResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound);

        // ADR-0062 §§ 3, 5: the portal's <HumanFromSelector> and per-Hat
        // inbox rendering both need the calling caller's bound-Human set
        // — every Human row whose `tenant_user_id` FK points at the
        // caller — plus the `IsPrimary` flag (from
        // `TenantUser.PrimaryHumanId`) so the new-outbound default
        // selection lights up the right Hat without a second round-trip.
        // The per-unit Memberships sub-list lets the selector render the
        // "designer in Magazine" context label inline.
        group.MapGet("/me/humans", ListCallerHumansAsync)
            .WithName("ListCallerHumans")
            .WithSummary("List the calling caller's bound Humans (Hats), optionally scoped to a recipient (ADR-0062 §§ 3, 5; #2972).")
            .WithDescription("Returns every Human row whose tenant_user_id FK points at the authenticated caller, with the IsPrimary flag set on the row that matches TenantUser.PrimaryHumanId. Each row carries a Memberships sub-list — one entry per UnitMembershipHuman row the Human appears on — so the portal's from-selector can render the per-Hat context label (e.g. designer in Magazine) in one round-trip. Supply one or more `recipient=<scheme:id>` (unit/agent) query parameters to scope the result to only the Hats that can reach those recipients under the Hat ↔ unit reachability rule (#2972) — the messaging from-selector / CLI `--as` resolution use this so the operator is never offered a Hat that cannot address the target. The disambiguated label is computed over the returned (scoped) set. Omitting the parameter returns the full bound set (the 'Your Hats' settings surface).")
            .Produces<IReadOnlyList<CallerHumanResponse>>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized);

        return group;
    }

    private static async Task<IResult> GetTenantUserAsync(
        Guid tenantUserId,
        SpringDbContext db,
        CancellationToken cancellationToken)
    {
        if (tenantUserId == Guid.Empty)
        {
            return Results.Problem(
                detail: "Tenant user id must not be empty.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        var row = await db.TenantUsers
            .AsNoTracking()
            .FirstOrDefaultAsync(u => u.Id == tenantUserId, cancellationToken);
        if (row is null)
        {
            return Results.Problem(
                detail: $"Tenant user '{tenantUserId:N}' was not found in the current tenant.",
                statusCode: StatusCodes.Status404NotFound);
        }

        return Results.Ok(ToResponse(row));
    }

    /// <summary>
    /// ADR-0062 § 6 / #2827: resolve a <c>TenantUser</c> by its OAuth
    /// <c>auth_subject</c> claim. Returns 404 when no row matches in the
    /// current tenant. Used by the CLI's <c>&lt;tenant-user-ref&gt;</c>
    /// parser when the operator passes a non-Guid, non-<c>me</c> string
    /// (e.g. <c>alice@example.com</c>) so the CLI can stamp the resolved
    /// id on a Hat-bind / package-install override.
    /// </summary>
    private static async Task<IResult> FindTenantUserByAuthSubjectAsync(
        [FromQuery] string? authSubject,
        SpringDbContext db,
        CancellationToken cancellationToken)
    {
        var subject = (authSubject ?? string.Empty).Trim();
        if (string.IsNullOrEmpty(subject))
        {
            return Results.Problem(
                detail: "Query parameter 'authSubject' is required and must not be empty.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        var row = await db.TenantUsers
            .AsNoTracking()
            .FirstOrDefaultAsync(u => u.AuthSubject == subject, cancellationToken);
        if (row is null)
        {
            return Results.Problem(
                detail: $"No tenant user with auth subject '{subject}' was found in the current tenant.",
                statusCode: StatusCodes.Status404NotFound);
        }

        return Results.Ok(ToResponse(row));
    }

    private static async Task<IResult> UpdateTenantUserAsync(
        Guid tenantUserId,
        [FromBody] UpdateTenantUserRequest? request,
        SpringDbContext db,
        CancellationToken cancellationToken)
    {
        if (tenantUserId == Guid.Empty)
        {
            return Results.Problem(
                detail: "Tenant user id must not be empty.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        var body = request ?? new UpdateTenantUserRequest();

        // Validate only when the caller actually supplied a new display
        // name — null means "leave unchanged" on the PATCH surface.
        if (body.DisplayName is not null)
        {
            var displayNameProblem = DisplayNameProblems.ValidateOrProblem(body.DisplayName);
            if (displayNameProblem is not null)
            {
                return displayNameProblem;
            }
        }

        var row = await db.TenantUsers
            .FirstOrDefaultAsync(u => u.Id == tenantUserId, cancellationToken);
        if (row is null)
        {
            return Results.Problem(
                detail: $"Tenant user '{tenantUserId:N}' was not found in the current tenant.",
                statusCode: StatusCodes.Status404NotFound);
        }

        if (body.DisplayName is not null)
        {
            row.DisplayName = body.DisplayName;
        }
        if (body.Description is not null)
        {
            row.Description = string.IsNullOrWhiteSpace(body.Description) ? null : body.Description.Trim();
        }

        await db.SaveChangesAsync(cancellationToken);

        return Results.Ok(ToResponse(row));
    }

    private static async Task<IResult> UpsertIdentityAsync(
        Guid tenantUserId,
        [FromBody] TenantUserConnectorIdentityRequest request,
        SpringDbContext db,
        CancellationToken cancellationToken)
    {
        if (request is null)
        {
            return Results.Problem(
                detail: "Request body is required.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        var connectorId = (request.ConnectorId ?? string.Empty).Trim();
        var username = (request.Username ?? string.Empty).Trim();
        var displayHandle = string.IsNullOrWhiteSpace(request.DisplayHandle)
            ? null
            : request.DisplayHandle.Trim();

        if (string.IsNullOrWhiteSpace(connectorId) || string.IsNullOrWhiteSpace(username))
        {
            return Results.Problem(
                detail: "Both 'connectorId' and 'username' are required.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        if (tenantUserId == Guid.Empty)
        {
            return Results.Problem(
                detail: "Tenant user id must not be empty.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        // Tenant query filter on the DbContext scopes this lookup
        // automatically — a cross-tenant id surfaces as a clean 404.
        var tenantUserExists = await db.TenantUsers
            .AsNoTracking()
            .AnyAsync(u => u.Id == tenantUserId, cancellationToken);
        if (!tenantUserExists)
        {
            return Results.Problem(
                detail: $"Tenant user '{tenantUserId:N}' was not found in the current tenant.",
                statusCode: StatusCodes.Status404NotFound);
        }

        // Existing row for the same (connector, username) — either the
        // same tenant user (in-place display-handle update) or a different
        // one (409 collision; the reverse-lookup unique constraint per
        // ADR-0047 §2 enforces "one connector login per tenant user").
        var existingByLogin = await db.TenantUserConnectorIdentities
            .FirstOrDefaultAsync(
                e => e.ConnectorId == connectorId && e.Username == username,
                cancellationToken);

        if (existingByLogin is not null && existingByLogin.TenantUserId != tenantUserId)
        {
            return Results.Problem(
                title: "Connector identity already claimed",
                detail: $"Connector identity '{connectorId}:{username}' is already mapped to a different tenant user.",
                statusCode: StatusCodes.Status409Conflict);
        }

        // Also check the natural-key row "this tenant user already has an
        // identity on this connector" — that path collapses to an
        // in-place update of (username, displayHandle) so re-running
        // `spring user identity set --connector github --username new`
        // overwrites instead of conflicting on the reverse-lookup index.
        var existingByPair = existingByLogin ?? await db.TenantUserConnectorIdentities
            .FirstOrDefaultAsync(
                e => e.TenantUserId == tenantUserId && e.ConnectorId == connectorId,
                cancellationToken);

        if (existingByPair is not null)
        {
            existingByPair.Username = username;
            existingByPair.DisplayHandle = displayHandle;
            await db.SaveChangesAsync(cancellationToken);
            return Results.Ok(ToResponse(existingByPair));
        }

        var row = new TenantUserConnectorIdentityEntity
        {
            Id = Guid.NewGuid(),
            TenantUserId = tenantUserId,
            ConnectorId = connectorId,
            Username = username,
            DisplayHandle = displayHandle,
        };

        try
        {
            db.TenantUserConnectorIdentities.Add(row);
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            // Race: a concurrent writer landed the same tuple between the
            // SELECT and INSERT. Either of the two unique indices may have
            // caught it.
            db.Entry(row).State = EntityState.Detached;
            return Results.Problem(
                title: "Connector identity already claimed",
                detail: $"Connector identity '{connectorId}:{username}' was registered concurrently by another caller.",
                statusCode: StatusCodes.Status409Conflict);
        }

        return Results.Ok(ToResponse(row));
    }

    private static async Task<IResult> ListIdentitiesAsync(
        Guid tenantUserId,
        SpringDbContext db,
        CancellationToken cancellationToken)
    {
        var rows = await db.TenantUserConnectorIdentities
            .AsNoTracking()
            .Where(e => e.TenantUserId == tenantUserId)
            .OrderBy(e => e.ConnectorId).ThenBy(e => e.Username)
            .ToListAsync(cancellationToken);

        return Results.Ok(rows.ConvertAll(ToResponse));
    }

    private static async Task<IResult> RemoveIdentityAsync(
        Guid tenantUserId,
        [FromQuery] string? connectorId,
        [FromQuery] string? username,
        SpringDbContext db,
        CancellationToken cancellationToken)
    {
        var c = (connectorId ?? string.Empty).Trim();
        var u = (username ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(c) || string.IsNullOrWhiteSpace(u))
        {
            return Results.Problem(
                detail: "Both 'connectorId' and 'username' query parameters are required.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        var row = await db.TenantUserConnectorIdentities
            .FirstOrDefaultAsync(
                e => e.TenantUserId == tenantUserId && e.ConnectorId == c && e.Username == u,
                cancellationToken);
        if (row is null)
        {
            // Idempotent contract: removing a row that doesn't exist still
            // returns 204 so the CLI doesn't have to branch on prior state.
            return Results.NoContent();
        }

        db.TenantUserConnectorIdentities.Remove(row);
        await db.SaveChangesAsync(cancellationToken);
        return Results.NoContent();
    }

    private static async Task<IResult> SetPrimaryHumanAsync(
        Guid tenantUserId,
        [FromBody] SetPrimaryHumanRequest? request,
        [FromServices] SpringDbContext db,
        CancellationToken cancellationToken)
    {
        if (request is null)
        {
            return Results.Problem(
                detail: "Request body is required.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        if (tenantUserId == Guid.Empty)
        {
            return Results.Problem(
                detail: "Tenant user id must not be empty.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        if (request.HumanId == Guid.Empty)
        {
            return Results.Problem(
                detail: "'humanId' is required and must not be empty.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        var tenantUserRow = await db.TenantUsers
            .FirstOrDefaultAsync(u => u.Id == tenantUserId, cancellationToken);
        if (tenantUserRow is null)
        {
            return Results.Problem(
                detail: $"Tenant user '{tenantUserId:N}' was not found in the current tenant.",
                statusCode: StatusCodes.Status404NotFound);
        }

        // ADR-0062 § 2: validate the binding so the operator gets a clean
        // 400 rather than the raw FK violation. The Humans tenant filter
        // ensures cross-tenant ids surface as a clean "not bound" rather
        // than leaking existence.
        var humanRow = await db.Humans
            .AsNoTracking()
            .FirstOrDefaultAsync(h => h.Id == request.HumanId, cancellationToken);
        if (humanRow is null || humanRow.TenantUserId != tenantUserId)
        {
            return Results.Problem(
                title: "Bad Request",
                detail:
                    $"Hat '{request.HumanId:N}' is not bound to TenantUser '{tenantUserId:N}'. " +
                    "Run `spring user identity list` to see your bound Hats.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        tenantUserRow.PrimaryHumanId = request.HumanId;
        await db.SaveChangesAsync(cancellationToken);

        return Results.Ok(new SetPrimaryHumanResponse(tenantUserId, request.HumanId));
    }

    private static async Task<IResult> ListCallerHumansAsync(
        IAuthenticatedCallerAccessor callerAccessor,
        IHatReachabilityService hatReachability,
        SpringDbContext db,
        [FromQuery(Name = "recipient")] string[]? recipient,
        CancellationToken cancellationToken)
    {
        // The route is gated to `TenantUser`, so a missing caller here
        // means the auth pipeline accepted the request without
        // surfacing a NameIdentifier claim — surface as 401 rather than
        // returning an empty list that would look like "no Hats yet".
        var callerAddress = await callerAccessor.GetCallerAddressAsync(cancellationToken);
        if (callerAddress is null
            || !string.Equals(callerAddress.Scheme, Cvoya.Spring.Core.Messaging.Address.TenantUserScheme, StringComparison.OrdinalIgnoreCase))
        {
            return Results.Problem(
                detail: "No authenticated TenantUser caller identity available.",
                statusCode: StatusCodes.Status401Unauthorized);
        }

        var callerTenantUserId = callerAddress.Id;

        // #2972: optional recipient scoping. Parse the `recipient=<scheme:id>`
        // query parameters; a malformed value is a 400 so the caller learns
        // the contract rather than silently getting the full set.
        var scopeTargets = new List<Cvoya.Spring.Core.Messaging.Address>();
        if (recipient is { Length: > 0 })
        {
            foreach (var raw in recipient)
            {
                if (string.IsNullOrWhiteSpace(raw))
                {
                    continue;
                }
                if (!Cvoya.Spring.Core.Messaging.Address.TryParse(raw, out var parsed) || parsed is null)
                {
                    return Results.Problem(
                        detail: $"Query parameter 'recipient' value '{raw}' is not a valid address (expected scheme:id).",
                        statusCode: StatusCodes.Status400BadRequest);
                }
                scopeTargets.Add(parsed);
            }
        }

        // The TenantUser row drives the IsPrimary flag; load it once so
        // the result-shaping step can stamp the flag in the same pass.
        var primaryHumanId = await db.TenantUsers
            .AsNoTracking()
            .Where(u => u.Id == callerTenantUserId)
            .Select(u => u.PrimaryHumanId)
            .FirstOrDefaultAsync(cancellationToken);

        var humans = await db.Humans
            .AsNoTracking()
            .Where(h => h.TenantUserId == callerTenantUserId)
            .Select(h => new { h.Id, h.Username, h.DisplayName })
            .ToListAsync(cancellationToken);

        // #2972: when scoped to recipient(s), keep only the Hats that can
        // reach them — the messaging from-selector / CLI `--as` resolution
        // must never offer a Hat that cannot address the target. The
        // disambiguated label below is then computed over this scoped set
        // (ADR-0062 § 5a "per result-set scope").
        if (scopeTargets.Count > 0)
        {
            var wearable = (await hatReachability.GetWearableHatsAsync(
                callerTenantUserId, scopeTargets, cancellationToken)).ToHashSet();
            humans = humans.Where(h => wearable.Contains(h.Id)).ToList();
        }

        if (humans.Count == 0)
        {
            return Results.Ok(Array.Empty<CallerHumanResponse>());
        }

        var humanIds = humans.Select(h => h.Id).ToList();

        // One round-trip for the membership rows so we can render the
        // per-Hat context label (e.g. "designer in Magazine") inline.
        // The join to `UnitDefinitions` resolves the unit's display
        // name; the membership table holds the role list verbatim.
        var memberships = await (
            from m in db.UnitMembershipsHumans.AsNoTracking()
            where humanIds.Contains(m.HumanId)
            join u in db.UnitDefinitions.AsNoTracking() on m.UnitId equals u.Id into uj
            from u in uj.DefaultIfEmpty()
            select new
            {
                m.HumanId,
                m.UnitId,
                UnitDisplayName = u != null ? u.DisplayName : string.Empty,
                m.Roles,
            }).ToListAsync(cancellationToken);

        var membershipsByHuman = memberships
            .GroupBy(m => m.HumanId)
            .ToDictionary(
                g => g.Key,
                g => (IReadOnlyList<CallerHumanMembershipResponse>)g
                    .OrderBy(x => x.UnitDisplayName, StringComparer.OrdinalIgnoreCase)
                    .Select(x => new CallerHumanMembershipResponse(
                        x.UnitId,
                        string.IsNullOrWhiteSpace(x.UnitDisplayName) ? "Unknown unit" : x.UnitDisplayName,
                        x.Roles ?? new List<string>()))
                    .ToList());

        // ADR-0062 § 5 / #2829: compute the disambiguated label once,
        // server-side, so the portal and the CLI render identical
        // strings. The scope is the caller's full bound set — the same
        // set the from-selector / CLI ambiguity prompt walks — so a
        // collision is detected the same way regardless of which surface
        // the operator is looking at. Pick the first membership's
        // (role, unit) for the disambiguator since the panel itself
        // orders memberships alphabetically by unit; same row also wins
        // the tie-break for the membership tier of the rule.
        var candidates = humans
            .Select(h =>
            {
                var ms = membershipsByHuman.TryGetValue(h.Id, out var msList)
                    ? msList
                    : Array.Empty<CallerHumanMembershipResponse>();
                var first = ms.Count > 0 ? ms[0] : null;
                return new HatLabelCandidate(
                    HumanId: h.Id,
                    BaseName: string.IsNullOrWhiteSpace(h.DisplayName) ? h.Username : h.DisplayName,
                    UnitDisplayName: first?.UnitDisplayName,
                    Roles: first?.Roles);
            })
            .ToList();
        var labels = HatLabelDisambiguator.DisambiguateAll(candidates);

        // Ordering: primary Hat first, then alphabetical by display name
        // so the selector reads predictably even when no Hat is pinned.
        var rows = humans
            .Select(h =>
            {
                var displayName = string.IsNullOrWhiteSpace(h.DisplayName) ? h.Username : h.DisplayName;
                return new CallerHumanResponse(
                    h.Id,
                    displayName,
                    labels.TryGetValue(h.Id, out var label) ? label : displayName,
                    primaryHumanId is { } pid && pid == h.Id,
                    membershipsByHuman.TryGetValue(h.Id, out var ms) ? ms : Array.Empty<CallerHumanMembershipResponse>());
            })
            .OrderByDescending(r => r.IsPrimary)
            .ThenBy(r => r.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return Results.Ok(rows);
    }

    private static TenantUserResponse ToResponse(TenantUserEntity row) =>
        new(row.Id, row.AuthSubject, row.DisplayName, row.Description, row.CreatedAt, row.UpdatedAt);

    private static TenantUserConnectorIdentityResponse ToResponse(TenantUserConnectorIdentityEntity row) =>
        new(row.TenantUserId, row.ConnectorId, row.Username, row.DisplayHandle, row.CreatedAt, row.UpdatedAt);
}
