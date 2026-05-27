// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Routing;

using System.Text.Json;

using Cvoya.Spring.Core;
using Cvoya.Spring.Core.Directory;
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
        IServiceScopeFactory scopeFactory)
    {
        ArgumentNullException.ThrowIfNull(scopeFactory);

        _directoryService = directoryService;
        _agentProxyResolver = agentProxyResolver;
        _permissionService = permissionService;
        _logger = loggerFactory.CreateLogger<MessageRouter>();
        _scopeFactory = scopeFactory;
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

        var (actorId, actorScheme) = resolution.Value!;

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
        if (string.Equals(actorScheme, Address.UnitScheme, StringComparison.OrdinalIgnoreCase)
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
    private async Task<Result<(string ActorId, string Scheme), RoutingError>> ResolveActorIdAsync(
        Address address, CancellationToken cancellationToken)
    {
        // Direct address: agent://@f47ac10b-... — extract UUID, no directory lookup.
        if (address.Path.StartsWith('@'))
        {
            var actorId = address.Path[1..];
            _logger.LogDebug("Resolved direct address {Scheme}://{Path} to actor ID {ActorId}",
                address.Scheme, address.Path, actorId);
            return Result<(string, string), RoutingError>.Success((actorId, address.Scheme));
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
                return Result<(string, string), RoutingError>.Failure(RoutingError.AddressNotFound(address));
            }

            _logger.LogDebug("Resolved human address {Scheme}://{Path} to actor ID {ActorId}",
                address.Scheme, address.Path, address.Path);
            return Result<(string, string), RoutingError>.Success((address.Path, "human"));
        }

        // Path address: look up in directory service.
        var entry = await _directoryService.ResolveAsync(address, cancellationToken);
        if (entry is null)
        {
            _logger.LogWarning("Address not found: {Scheme}://{Path}", address.Scheme, address.Path);
            return Result<(string, string), RoutingError>.Failure(RoutingError.AddressNotFound(address));
        }

        var actorIdString = Cvoya.Spring.Core.Identifiers.GuidFormatter.Format(entry.ActorId);
        _logger.LogDebug("Resolved path address {Scheme}://{Path} to actor ID {ActorId}",
            address.Scheme, address.Path, actorIdString);
        return Result<(string, string), RoutingError>.Success((actorIdString, address.Scheme));
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
