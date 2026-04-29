// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Routing;

using System.Text.Json;

using Cvoya.Spring.Core;
using Cvoya.Spring.Core.Directory;
using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Dapr.Actors;
using Cvoya.Spring.Dapr.Auth;

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
public class MessageRouter(
    IDirectoryService directoryService,
    IAgentProxyResolver agentProxyResolver,
    IPermissionService permissionService,
    ILoggerFactory loggerFactory) : IMessageRouter
{
    private readonly ILogger _logger = loggerFactory.CreateLogger<MessageRouter>();

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
        if (string.Equals(destination.Scheme, "role", StringComparison.OrdinalIgnoreCase))
        {
            return await RouteMulticastAsync(message, cancellationToken);
        }

        // Resolve the actor ID from the address.
        var resolution = await ResolveActorIdAsync(destination, cancellationToken);
        if (!resolution.IsSuccess)
        {
            return Result<Message?, RoutingError>.Failure(resolution.Error!);
        }

        var (actorId, actorScheme) = resolution.Value!;

        // Permission check: if the destination is a unit and the sender is a human,
        // verify the human has at least Viewer permission in the unit.
        if (string.Equals(actorScheme, "unit", StringComparison.OrdinalIgnoreCase)
            && string.Equals(message.From.Scheme, "human", StringComparison.OrdinalIgnoreCase))
        {
            var permissionCheck = await CheckUnitPermissionAsync(
                message.From.Path, actorId, PermissionLevel.Viewer, message.To, cancellationToken);
            if (!permissionCheck.IsSuccess)
            {
                return Result<Message?, RoutingError>.Failure(permissionCheck.Error!);
            }
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
        var entry = await directoryService.ResolveAsync(address, cancellationToken);
        if (entry is null)
        {
            _logger.LogWarning("Address not found: {Scheme}://{Path}", address.Scheme, address.Path);
            return Result<(string, string), RoutingError>.Failure(RoutingError.AddressNotFound(address));
        }

        _logger.LogDebug("Resolved path address {Scheme}://{Path} to actor ID {ActorId}",
            address.Scheme, address.Path, entry.ActorId);
        return Result<(string, string), RoutingError>.Success((entry.ActorId, address.Scheme));
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
        var proxy = agentProxyResolver.Resolve(scheme, actorId);
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
    /// Checks that a human has at least the specified permission level in a unit.
    /// </summary>
    private async Task<Result<bool, RoutingError>> CheckUnitPermissionAsync(
        string humanId, string unitId, PermissionLevel minimumLevel,
        Address targetAddress, CancellationToken cancellationToken)
    {
        // Hierarchy-aware check (#414): a human with Operator rights on a
        // parent unit can address the parent's descendant units without a
        // direct grant on each one, subject to per-unit
        // UnitPermissionInheritance. Direct grants on the target unit still
        // take precedence.
        var permission = await permissionService.ResolveEffectivePermissionAsync(humanId, unitId, cancellationToken);

        if (permission is null || permission.Value < minimumLevel)
        {
            _logger.LogWarning(
                "Permission denied: human {HumanId} requires {Required} but has {Actual} in unit {UnitId}",
                humanId, minimumLevel, permission?.ToString() ?? "none", unitId);
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
        var entries = await directoryService.ResolveByRoleAsync(role, cancellationToken);

        if (entries.Count == 0)
        {
            _logger.LogWarning("No actors found for role {Role}", role);
            return Result<Message?, RoutingError>.Failure(RoutingError.AddressNotFound(message.To));
        }

        _logger.LogInformation("Multicasting message {MessageId} to {Count} actors with role {Role}",
            message.Id, entries.Count, role);

        var tasks = entries.Select(entry =>
            DeliverAsync(message, entry.ActorId, entry.Address.Scheme, cancellationToken));

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
}