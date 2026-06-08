// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Routing;

using System.Text.Json;

using Cvoya.Spring.Core;
using Cvoya.Spring.Core.Artefacts;
using Cvoya.Spring.Core.Directory;
using Cvoya.Spring.Core.Lifecycle;
using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Dapr.Actors;
using Cvoya.Spring.Dapr.Auth;
using Cvoya.Spring.Dapr.Threads;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

/// <summary>
/// Resolves <see cref="Address"/> instances to Dapr actor proxies and delivers messages.
/// Supports path-based resolution via <see cref="IDirectoryService"/>, direct UUID addresses,
/// and multicast delivery for role-based addresses.
/// <para>
/// Delivery goes through the shared <see cref="IAgentProxyResolver"/>
/// abstraction: every actor-shaped scheme resolves to the same
/// <see cref="Actors.IAgent"/> contract, so the router does not need to
/// switch on <c>agent://</c> vs <c>unit://</c> vs <c>human://</c> vs
/// <c>connector://</c> to dispatch <c>ReceiveAsync</c>.
/// </para>
/// </summary>
public class MessageRouter : IMessageRouter
{
    private readonly IDirectoryService _directoryService;
    private readonly IAgentProxyResolver _agentProxyResolver;
    private readonly IPermissionService _permissionService;
    private readonly ILogger _logger;
    private readonly IServiceScopeFactory _scopeFactory;
    // #2981: queryable lifecycle-status mirror used to refuse delivery to a
    // stopped recipient (the receive half of an authoritative stop, the
    // chokepoint that breaks a stopped unit's self-sustaining conversation).
    // Optional so the many direct test constructions keep compiling; null
    // fails open (the recipient actor's own receive gate is the authority).
    private readonly ILifecycleStatusStore? _lifecycleStatusStore;

    /// <summary>
    /// Constructs a router. The <paramref name="scopeFactory"/> opens a
    /// scoped DI container per dispatch so the singleton router can resolve
    /// the scoped <see cref="Threads.IMessageWriter"/> for the EF-backed
    /// <c>messages</c> table write (#2053 / ADR-0030).
    /// </summary>
    public MessageRouter(
        IDirectoryService directoryService,
        IAgentProxyResolver agentProxyResolver,
        IPermissionService permissionService,
        ILoggerFactory loggerFactory,
        IServiceScopeFactory scopeFactory,
        ILifecycleStatusStore? lifecycleStatusStore = null)
    {
        ArgumentNullException.ThrowIfNull(scopeFactory);

        _directoryService = directoryService;
        _agentProxyResolver = agentProxyResolver;
        _permissionService = permissionService;
        _logger = loggerFactory.CreateLogger<MessageRouter>();
        _scopeFactory = scopeFactory;
        _lifecycleStatusStore = lifecycleStatusStore;
    }

    /// <summary>
    /// Routes a message to its destination actor and returns the response.
    /// </summary>
    /// <param name="message">The message to route.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>A result containing the actor's response or a routing error.</returns>
    public virtual async Task<Result<Message?, RoutingError>> RouteAsync(Message message, CancellationToken cancellationToken = default)
    {
        var destination = message.To;

        // Multicast: role:// addresses fan out to all actors with that role.
        // Resolved separately because role addresses have many actor ids and
        // the unit-permission gate doesn't apply.
        if (string.Equals(destination.Scheme, "role", StringComparison.OrdinalIgnoreCase))
        {
            if (IMessageWriter.ShouldWrite(message))
            {
                await PersistMessageAsync(message, cancellationToken);
            }
            return await RouteMulticastAsync(message, cancellationToken);
        }

        // Resolve the actor ID from the address before persisting so the
        // permission gate (below) can run on the resolved unit guid. The
        // resolution itself is read-only — no DB writes — so a forbidden
        // send still leaves the timeline clean (#2859).
        var resolution = await ResolveActorIdAsync(destination, cancellationToken);
        if (!resolution.IsSuccess)
        {
            return Result<Message?, RoutingError>.Failure(resolution.Error!);
        }

        var (actorId, actorScheme, resolvedEntry) = resolution.Value!;

        // #3133 / #2084: the type that drives the type-dependent gates below
        // (the unit-permission gate and the stopped-recipient kind) is the
        // recipient's *actual* kind per the directory's DB/cache, not the
        // scheme claimed on the inbound address. For a path address the
        // directory entry was resolved out of the scheme-named table, so the
        // entry's scheme IS the DB-confirmed kind — read it from there. For
        // the human:// short-circuit and the direct '@' form (neither of
        // which consult the directory) there is no entry; those keep the
        // address scheme, which is correct — a human is 1:1 with its address
        // (deliberately not directory-registered) and is never a unit, and
        // the '@' form is a non-directory diagnostic shape. This deliberately
        // does NOT issue a second IDirectoryService.ResolveKindAsync(id) by
        // id: the seam probes unit-then-agent, so an id-keyed lookup would add
        // a guaranteed unit-table miss (a per-message DB hit) for every
        // agent-addressed domain message on this hot path. The entry the
        // scheme-keyed resolve already returned is the same DB/cache answer at
        // zero additional cost.
        var recipientScheme = resolvedEntry is not null
            ? resolvedEntry.Address.Scheme
            : actorScheme;

        // Permission check: when the destination is a unit and the sender is
        // a permission-bearing principal (human or tenant-user per ADR-0047
        // §1 / #2768), verify the caller has at least Viewer permission on
        // the unit. The OSS PermissionService short-circuits tenant-user to
        // implicit Owner; the cloud overlay can swap in a per-tenant-user
        // grant lookup via DI without touching this gate.
        //
        // #2859: this gate runs BEFORE PersistMessageAsync. A 403 must leave
        // the messages table — and any Activity event the endpoint emits —
        // free of phantom rows for the denied attempt. ADR-0030's
        // "persist-on-delivery-failure" invariant is preserved for genuine
        // delivery exceptions (DeliverAsync throws after the persist below);
        // the reorder narrows it to "persist iff the caller is allowed to
        // send."
        if (string.Equals(recipientScheme, Address.UnitScheme, StringComparison.OrdinalIgnoreCase)
            && (string.Equals(message.From.Scheme, Address.HumanScheme, StringComparison.OrdinalIgnoreCase)
                || string.Equals(message.From.Scheme, Address.TenantUserScheme, StringComparison.OrdinalIgnoreCase)))
        {
            if (!Cvoya.Spring.Core.Identifiers.GuidFormatter.TryParse(actorId, out var unitGuid))
            {
                // ResolveActorIdAsync always emits the canonical 32-hex form,
                // so a parse failure here means a directory entry written by
                // a non-canonical writer. Treat as not-found rather than
                // throwing — the message would not reach the actor anyway.
                _logger.LogWarning(
                    "Permission gate: unit actor id {ActorId} did not parse as Guid; treating as not-found.",
                    actorId);
                return Result<Message?, RoutingError>.Failure(RoutingError.AddressNotFound(message.To));
            }
            var permissionCheck = await CheckUnitPermissionAsync(
                message.From, unitGuid, PermissionLevel.Viewer, message.To, cancellationToken);
            if (!permissionCheck.IsSuccess)
            {
                return Result<Message?, RoutingError>.Failure(permissionCheck.Error!);
            }
        }

        // #2981 / subsumed #2978: refuse delivery to a stopped recipient. A
        // halted unit/agent must not receive messages — the central chokepoint
        // that keeps a stopped unit's in-flight conversation from re-delivering
        // into stopped members and cold-starting their containers. Domain
        // messages only: control messages (Cancel, HealthCheck, StatusQuery, …)
        // must still reach a stopping artefact so the stop itself can drain.
        // Placed before PersistMessageAsync so a refused send leaves the
        // messages table clean, mirroring the permission gate (#2859). Fails
        // open on a missing mirror row / read error — the recipient actor's own
        // receive gate is the authority.
        if (message.Type == MessageType.Domain
            && await IsRecipientHaltedAsync(actorId, recipientScheme, cancellationToken))
        {
            _logger.LogInformation(
                "Refusing delivery of message {MessageId} to {Scheme}://{Path}: recipient is stopped (#2981).",
                message.Id, message.To.Scheme, message.To.Path);
            return Result<Message?, RoutingError>.Failure(RoutingError.RecipientStopped(message.To));
        }

        // #2053 / ADR-0030: persist the message envelope before delivery so
        // the Thread Timeline has a durable row even if the downstream
        // recipient throws. The write is keyed on Message.Id and idempotent —
        // re-dispatch (manual retry) hits the existence check inside the
        // writer rather than duplicating history. Skipped for control /
        // non-Domain messages (HealthCheck, Cancel, …) and for messages that
        // arrive without a Guid-shaped thread id; the API path validates the
        // shape before calling, so the skip is a defensive guard.
        //
        // #2859: this write now happens AFTER the permission gate above so a
        // 403 leaves the messages table clean. ADR-0030's persist-on-failure
        // invariant continues to apply to delivery exceptions (DeliverAsync
        // throws after this point) — only the authorization-rejection path
        // skips persistence.
        if (IMessageWriter.ShouldWrite(message))
        {
            await PersistMessageAsync(message, cancellationToken);
        }

        return await DeliverAsync(message, actorId, actorScheme, cancellationToken);
    }

    /// <summary>
    /// Resolves an address to its actor ID and the scheme used for actor type lookup.
    /// Direct addresses (path starts with '@') skip directory lookup.
    /// <para>
    /// <c>human://</c> addresses also skip the directory: humans are 1:1 with their
    /// address (the path IS the human identifier), so there is no routing
    /// indirection that a directory lookup could add. The platform has no
    /// general flow that registers humans in the directory, and forcing one
    /// would just trade a real bug (#1037) for a registration bookkeeping
    /// problem.
    /// </para>
    /// </summary>
    private async Task<Result<(string ActorId, string Scheme, DirectoryEntry? Entry), RoutingError>> ResolveActorIdAsync(
        Address address, CancellationToken cancellationToken)
    {
        // Direct address: agent://@f47ac10b-... — extract UUID, no directory lookup.
        // No directory entry is resolved, so the caller falls back to the
        // address scheme for the type-dependent gates (#3133).
        if (address.Path.StartsWith('@'))
        {
            var actorId = address.Path[1..];
            _logger.LogDebug("Resolved direct address {Scheme}://{Path} to actor ID {ActorId}",
                address.Scheme, address.Path, actorId);
            return Result<(string, string, DirectoryEntry?), RoutingError>.Success((actorId, address.Scheme, null));
        }

        // Human address: the path IS the actor id — no directory indirection.
        // See #1037: in LocalDev mode the worker tried to route an agent's
        // response back to human://local-dev-user and failed because no
        // directory entry exists. Short-circuiting here generalises the fix
        // beyond local-dev to every human:// caller.
        if (string.Equals(address.Scheme, "human", StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrEmpty(address.Path))
            {
                _logger.LogWarning("Human address has empty path: {Scheme}://", address.Scheme);
                return Result<(string, string, DirectoryEntry?), RoutingError>.Failure(RoutingError.AddressNotFound(address));
            }

            _logger.LogDebug("Resolved human address {Scheme}://{Path} to actor ID {ActorId}",
                address.Scheme, address.Path, address.Path);
            // No directory entry (humans are deliberately not registered); the
            // caller keeps the human scheme for the type-dependent gates (#3133).
            return Result<(string, string, DirectoryEntry?), RoutingError>.Success((address.Path, "human", null));
        }

        // Path address: look up in directory service. The entry is resolved
        // out of the scheme-named table, so a non-null result both confirms
        // existence AND confirms the recipient's actual kind (== its scheme)
        // per the DB/cache — the caller reads that authoritative kind off the
        // entry for the type-dependent gates instead of trusting the claimed
        // address scheme (#3133 / #2084).
        var entry = await _directoryService.ResolveAsync(address, cancellationToken);
        if (entry is null)
        {
            _logger.LogWarning("Address not found: {Scheme}://{Path}", address.Scheme, address.Path);
            return Result<(string, string, DirectoryEntry?), RoutingError>.Failure(RoutingError.AddressNotFound(address));
        }

        var actorIdString = Cvoya.Spring.Core.Identifiers.GuidFormatter.Format(entry.ActorId);
        _logger.LogDebug("Resolved path address {Scheme}://{Path} to actor ID {ActorId}",
            address.Scheme, address.Path, actorIdString);
        return Result<(string, string, DirectoryEntry?), RoutingError>.Success((actorIdString, address.Scheme, entry));
    }

    /// <summary>
    /// #2981: returns <c>true</c> when the unit/agent recipient identified by
    /// <paramref name="actorId"/> / <paramref name="scheme"/> has a mirrored
    /// lifecycle status that is halted (Stopped / Stopping / Error). Returns
    /// <c>false</c> — allow delivery — for non-actor schemes (human, role,
    /// connector, tenant-user, which have no lifecycle mirror), an unparseable
    /// id, a missing mirror row, or a read error. The recipient actor's own
    /// receive gate is the authority; this is the early short-circuit that
    /// stops a cold-start before the actor is even reached.
    /// </summary>
    private async Task<bool> IsRecipientHaltedAsync(
        string actorId, string scheme, CancellationToken cancellationToken)
    {
        if (_lifecycleStatusStore is null)
        {
            return false;
        }

        ArtefactKind kind;
        if (string.Equals(scheme, Address.UnitScheme, StringComparison.OrdinalIgnoreCase))
        {
            kind = ArtefactKind.Unit;
        }
        else if (string.Equals(scheme, Address.AgentScheme, StringComparison.OrdinalIgnoreCase))
        {
            kind = ArtefactKind.Agent;
        }
        else
        {
            return false;
        }

        if (!Cvoya.Spring.Core.Identifiers.GuidFormatter.TryParse(actorId, out var artefactGuid))
        {
            return false;
        }

        try
        {
            var status = await _lifecycleStatusStore.TryGetStatusAsync(kind, artefactGuid, cancellationToken);
            return status is not null && status.Value.IsHalted();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Lifecycle mirror read failed for {Scheme} {ActorId}; allowing delivery (fail-open, #2981).",
                scheme, actorId);
            return false;
        }
    }

    /// <summary>
    /// Delivers a message to a single actor identified by its actor ID and scheme.
    /// The actor is obtained as an <see cref="Actors.IAgent"/> proxy via
    /// <see cref="IAgentProxyResolver"/>, so this method does not branch on
    /// scheme to dispatch <c>ReceiveAsync</c>.
    /// </summary>
    private async Task<Result<Message?, RoutingError>> DeliverAsync(
        Message message, string actorId, string scheme, CancellationToken cancellationToken)
    {
        var proxy = _agentProxyResolver.Resolve(scheme, actorId);
        if (proxy is null)
        {
            return Result<Message?, RoutingError>.Failure(
                RoutingError.AddressNotFound(message.To));
        }

        try
        {
            var response = await proxy.ReceiveAsync(message, cancellationToken);
            return Result<Message?, RoutingError>.Success(response);
        }
        catch (CallerValidationException ex)
        {
            // In-process path (tests + cloud-hosted overlay that wires a
            // non-Dapr proxy): the original exception type arrives intact.
            // #993: classify as 400-worthy, not 502.
            _logger.LogInformation(
                "Caller-side validation failed for {Scheme}://{Path} (actor {ActorId}): {Code} — {Detail}",
                message.To.Scheme, message.To.Path, actorId, ex.Code, ex.Detail);
            return Result<Message?, RoutingError>.Failure(
                RoutingError.CallerValidation(message.To, ex.Code, ex.Detail));
        }
        catch (Exception ex) when (CallerValidationException.TryParseMessage(ex.Message, out var code, out var detail))
        {
            // Cross-remoting path: Dapr actor-remoting wraps the original
            // exception in an ActorInvokeException and drops custom
            // properties, but preserves the message string. We encoded the
            // code into the message precisely so the classification
            // survives that hop. See CallerValidationException for the
            // wire-compat rationale.
            _logger.LogInformation(
                "Caller-side validation failed (remoted) for {Scheme}://{Path} (actor {ActorId}): {Code} — {Detail}",
                message.To.Scheme, message.To.Path, actorId, code, detail);
            return Result<Message?, RoutingError>.Failure(
                RoutingError.CallerValidation(message.To, code, detail));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Delivery failed for {Scheme}://{Path} (actor {ActorId})",
                message.To.Scheme, message.To.Path, actorId);
            return Result<Message?, RoutingError>.Failure(
                RoutingError.DeliveryFailed(message.To, ex.Message));
        }
    }

    /// <summary>
    /// Checks that a caller has at least the specified permission level on a
    /// unit. The OSS <see cref="IPermissionService"/> short-circuits
    /// tenant-user senders to implicit Owner; human senders fall through to
    /// the existing unit_human_permissions lookup. Cloud overlays may swap
    /// the permission service via DI to wire a per-tenant-user grant model.
    /// </summary>
    private async Task<Result<bool, RoutingError>> CheckUnitPermissionAsync(
        Address caller, Guid unitId, PermissionLevel minimumLevel,
        Address targetAddress, CancellationToken cancellationToken)
    {
        // Hierarchy-aware check (#414): a caller with Operator rights on a
        // parent unit can address the parent's descendant units without a
        // direct grant on each one, subject to per-unit
        // UnitPermissionInheritance. Direct grants on the target unit still
        // take precedence.
        var permission = await _permissionService.ResolveEffectivePermissionAsync(caller, unitId, cancellationToken);

        if (permission is null || permission.Value < minimumLevel)
        {
            _logger.LogWarning(
                "Permission denied: caller {Caller} requires {Required} but has {Actual} in unit {UnitId}",
                caller, minimumLevel, permission?.ToString() ?? "none", unitId);
            return Result<bool, RoutingError>.Failure(RoutingError.PermissionDenied(targetAddress));
        }

        return Result<bool, RoutingError>.Success(true);
    }

    /// <summary>
    /// Handles multicast delivery for role:// addresses by resolving all actors with the
    /// specified role and delivering the message to each one.
    /// Returns the first non-null response, or null if no actors responded.
    /// </summary>
    private async Task<Result<Message?, RoutingError>> RouteMulticastAsync(
        Message message, CancellationToken cancellationToken)
    {
        var role = message.To.Path;
        var entries = await _directoryService.ResolveByRoleAsync(role, cancellationToken);

        if (entries.Count == 0)
        {
            _logger.LogWarning("No actors found for role {Role}", role);
            return Result<Message?, RoutingError>.Failure(RoutingError.AddressNotFound(message.To));
        }

        _logger.LogInformation("Multicasting message {MessageId} to {Count} actors with role {Role}",
            message.Id, entries.Count, role);

        var tasks = entries.Select(entry =>
            DeliverAsync(message, Cvoya.Spring.Core.Identifiers.GuidFormatter.Format(entry.ActorId), entry.Address.Scheme, cancellationToken));

        var results = await Task.WhenAll(tasks);

        // Collect all successful responses.
        var responses = results
            .Where(r => r.IsSuccess && r.Value is not null)
            .Select(r => r.Value!)
            .ToList();

        if (responses.Count == 0)
        {
            // Check if any deliveries failed.
            var firstError = results.FirstOrDefault(r => !r.IsSuccess);
            if (firstError is { IsSuccess: false })
            {
                return Result<Message?, RoutingError>.Failure(firstError.Error!);
            }

            return Result<Message?, RoutingError>.Success(null);
        }

        // For multicast, aggregate responses into a single message with an array payload.
        if (responses.Count == 1)
        {
            return Result<Message?, RoutingError>.Success(responses[0]);
        }

        var aggregatedPayload = JsonSerializer.SerializeToElement(
            responses.Select(r => r.Payload).ToList());

        var aggregatedResponse = new Message(
            Guid.NewGuid(),
            message.To,
            message.From,
            MessageType.Domain,
            message.ThreadId,
            aggregatedPayload,
            DateTimeOffset.UtcNow);

        return Result<Message?, RoutingError>.Success(aggregatedResponse);
    }

    /// <summary>
    /// Resolves an <see cref="IMessageWriter"/> for the current request scope
    /// and writes the message envelope. The router is registered as a
    /// singleton so it cannot inject the scoped <c>SpringDbContext</c>
    /// directly; an <see cref="IServiceScopeFactory"/> opens a scope per
    /// dispatch (matching the pattern used by
    /// <see cref="PermissionService"/>).
    /// <para>
    /// Persistence is fail-fast: ADR-0040 makes the EF <c>messages</c> table
    /// authoritative for thread history, so a write failure must abort the
    /// dispatch rather than silently deliver an unrecorded message.
    /// Exceptions propagate to the caller; endpoints surface them as 5xx.
    /// </para>
    /// </summary>
    private async Task PersistMessageAsync(Message message, CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var writer = scope.ServiceProvider.GetRequiredService<IMessageWriter>();
        await writer.WriteAsync(message, cancellationToken);
    }
}
