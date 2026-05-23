// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Actors;

using System.Linq;
using System.Text.Json;

using Cvoya.Spring.Core;
using Cvoya.Spring.Core.Capabilities;
using Cvoya.Spring.Core.Identifiers;
using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Dapr.Data;
using Cvoya.Spring.Dapr.Data.Entities;

using global::Dapr.Actors.Runtime;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

/// <summary>
/// Dapr virtual actor representing a human user in the Spring Voyage platform.
/// Humans have identity, permission levels, and notification preferences.
/// Domain messages are rejected for viewers; all other permission levels receive
/// an acknowledgment (notification routing is future work).
/// </summary>
/// <remarks>
/// Per <a href="../../../docs/decisions/0040-actor-state-ownership-matrix.md">ADR-0040</a>,
/// the human's identity, global permission level, and notification preferences
/// are EF-authoritative on <see cref="HumanEntity"/>. The actor reads from
/// <see cref="SpringDbContext"/> on every call (one read; no warm cache in v0.1)
/// and never persists those facts to <see cref="IActorStateManager"/>.
/// Per-thread read cursors and unit-scoped permission entries remain in
/// actor state — those are runtime-ephemeral / not yet migrated and are
/// covered by separate Wave 1 issues.
/// </remarks>
public class HumanActor(
    ActorHost host,
    IActivityEventBus activityEventBus,
    IServiceScopeFactory scopeFactory,
    ILoggerFactory loggerFactory) : Actor(host), IHumanActor
{
    private readonly ILogger _logger = loggerFactory.CreateLogger<HumanActor>();

    /// <summary>
    /// Gets the address of this human actor. When the actor id is a UUID
    /// (the post-#1491 form) the address is emitted as the stable identity
    /// form <c>human:id:&lt;uuid&gt;</c>; legacy username-keyed actors
    /// (deployed before the migration ran) fall back to the navigation form
    /// <c>human://&lt;username&gt;</c>.
    /// </summary>
    public Address Address => Address.For("human", Id.GetId());

    /// <inheritdoc />
    public async Task<Message?> ReceiveAsync(Message message, CancellationToken cancellationToken = default)
    {
        try
        {
            // #456: humans are first-class observers — every domain message
            // addressed to them is published to the activity bus as a
            // MessageArrived event, correlated to the thread id. This
            // is what the Inbox query service consumes to answer "what's
            // waiting on me?". Keep the emission on the hot path (before the
            // rejection branch below) so a denied message still leaves an
            // audit trail.
            // #1209: persist the message envelope (sender / recipient /
            // payload) on the event so `spring inbox show` can render the
            // body inline, not just the summary line.
            // #1636: the summary line is the actual message text (or a
            // short non-leaky placeholder) — never the legacy
            // "Received Domain message <uuid> from <address>" envelope,
            // which leaks GUIDs into every downstream surface.
            if (message.Type == MessageType.Domain)
            {
                await EmitActivityEventAsync(
                    ActivityEventType.MessageArrived,
                    MessageArrivedDetails.BuildSummary(message),
                    cancellationToken,
                    details: MessageArrivedDetails.Build(message),
                    correlationId: message.ThreadId);
            }

            return message.Type switch
            {
                MessageType.StatusQuery => await HandleStatusQueryAsync(message, cancellationToken),
                MessageType.HealthCheck => HandleHealthCheck(message),
                MessageType.Domain => await HandleDomainMessageAsync(message, cancellationToken),
                _ => throw new CallerValidationException(
                    CallerValidationCodes.UnknownMessageType,
                    $"Unknown message type: {message.Type}")
            };
        }
        catch (Exception ex) when (ex is not SpringException)
        {
            _logger.LogError(ex, "Unhandled exception processing message {MessageId} of type {MessageType} in human actor {ActorId}",
                message.Id, message.Type, Id.GetId());
            return CreateErrorResponse(message, ex.Message);
        }
    }

    /// <summary>
    /// Publishes a single activity event on behalf of this human actor. Errors
    /// are swallowed (same pattern as <see cref="AgentActor"/> /
    /// <see cref="UnitActor"/>) so a failing observability pipeline never
    /// tears down message delivery.
    /// </summary>
    private async Task EmitActivityEventAsync(
        ActivityEventType eventType,
        string description,
        CancellationToken cancellationToken,
        JsonElement? details = null,
        string? correlationId = null)
    {
        try
        {
            var severity = eventType switch
            {
                ActivityEventType.ErrorOccurred => ActivitySeverity.Error,
                _ => ActivitySeverity.Info,
            };

            var evt = new ActivityEvent(
                Id: Guid.NewGuid(),
                Timestamp: DateTimeOffset.UtcNow,
                Source: Address,
                EventType: eventType,
                Severity: severity,
                Summary: description,
                Details: details,
                CorrelationId: correlationId,
                Cost: null);

            await activityEventBus.PublishAsync(evt, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Failed to emit {EventType} activity event from human actor {ActorId}",
                eventType, Id.GetId());
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// Defaults to <see cref="PermissionLevel.Operator"/> when the row
    /// has no explicit permission level set (or the row is missing).
    /// The previous default (<see cref="PermissionLevel.Viewer"/>) caused
    /// inbound Domain messages — including the agent's reply to a thread
    /// the user themselves started — to be rejected with "insufficient
    /// permission (Viewer)", breaking the new-conversation round-trip in
    /// the OSS deployment (#1473, #1476).
    ///
    /// Defaulting to Operator is an interim OSS unblocker, NOT the
    /// long-term shape of the permission model. Tracked under #1479,
    /// which redesigns the model around owner-by-creation and
    /// thread-scoped participation. Once that lands this default goes
    /// away and the per-resource ownership / per-thread membership
    /// records become the source of truth.
    /// </remarks>
    public async Task<PermissionLevel> GetPermissionAsync(CancellationToken cancellationToken = default)
    {
        var entity = await LoadHumanEntityAsync(cancellationToken);
        return entity?.PermissionLevel ?? PermissionLevel.Operator;
    }

    /// <summary>
    /// Sets the permission level for this human actor on the EF-authoritative
    /// <see cref="HumanEntity"/> row. Creates the row if it does not yet exist
    /// (the actor may be addressed by id before any other write site has
    /// materialised the row).
    /// </summary>
    /// <param name="level">The new permission level.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    public async Task SetPermissionAsync(PermissionLevel level, CancellationToken cancellationToken = default)
    {
        await UpsertHumanEntityAsync(
            entity => entity.PermissionLevel = level,
            cancellationToken);

        _logger.LogInformation("Human actor {ActorId} permission changed to {Permission}", Id.GetId(), level);
    }

    /// <summary>
    /// Returns the human's current notification preferences, or <c>null</c>
    /// when no explicit preferences have been written. Routing layers apply
    /// the platform default in the <c>null</c> case.
    /// </summary>
    public async Task<NotificationPreferences?> GetNotificationPreferencesAsync(
        CancellationToken cancellationToken = default)
    {
        var entity = await LoadHumanEntityAsync(cancellationToken);
        return entity?.NotificationPreferences;
    }

    /// <summary>
    /// Persists <paramref name="preferences"/> as the human's notification
    /// preferences on the EF-authoritative <see cref="HumanEntity"/> row.
    /// Pass <c>null</c> to clear preferences (routing falls back to the
    /// platform default).
    /// </summary>
    public async Task SetNotificationPreferencesAsync(
        NotificationPreferences? preferences,
        CancellationToken cancellationToken = default)
    {
        await UpsertHumanEntityAsync(
            entity => entity.NotificationPreferences = preferences,
            cancellationToken);

        _logger.LogInformation(
            "Human actor {ActorId} notification preferences updated",
            Id.GetId());
    }

    /// <inheritdoc />
    public async Task MarkReadAsync(string threadId, DateTimeOffset readAt, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(threadId))
        {
            return;
        }

        var map = await GetLastReadAtMapAsync(cancellationToken);

        // Only advance the cursor — never move it backwards.
        if (map.TryGetValue(threadId, out var existing) && existing >= readAt)
        {
            return;
        }

        map[threadId] = readAt;
        await StateManager.SetStateAsync(StateKeys.HumanLastReadAt, map, cancellationToken);

        _logger.LogDebug(
            "Human actor {ActorId} marked thread {ThreadId} as read at {ReadAt}",
            Id.GetId(), threadId, readAt);
    }

    /// <inheritdoc />
    public async Task<ThreadReadEntry[]> GetLastReadAtAsync(CancellationToken cancellationToken = default)
    {
        var map = await GetLastReadAtMapAsync(cancellationToken);
        return [.. map.Select(kv => new ThreadReadEntry(kv.Key, kv.Value))];
    }

    /// <summary>
    /// Retrieves the per-thread last-read-at map from state. Returns a mutable
    /// dictionary; callers that write to it must persist the result explicitly.
    /// </summary>
    private async Task<Dictionary<string, DateTimeOffset>> GetLastReadAtMapAsync(CancellationToken cancellationToken)
    {
        var result = await StateManager
            .TryGetStateAsync<Dictionary<string, DateTimeOffset>>(StateKeys.HumanLastReadAt, cancellationToken);

        return result.HasValue ? result.Value : [];
    }

    /// <summary>
    /// Loads the EF row keyed by this actor's id, or <c>null</c> when the
    /// actor id does not parse as a Guid (legacy username-keyed actors) or
    /// no row has been materialised yet for that id.
    /// </summary>
    private async Task<HumanEntity?> LoadHumanEntityAsync(CancellationToken cancellationToken)
    {
        if (!GuidFormatter.TryParse(Id.GetId(), out var humanId))
        {
            // Legacy username-keyed actor — the EF row is keyed by Guid;
            // there's nothing to read. Callers fall back to defaults.
            return null;
        }

        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<SpringDbContext>();
        return await db.Humans
            .AsNoTracking()
            .FirstOrDefaultAsync(h => h.Id == humanId, cancellationToken);
    }

    /// <summary>
    /// Reads the row keyed by this actor's id (if any), applies
    /// <paramref name="mutate"/>, and saves. When no row exists yet, an
    /// upsert creates one — the actor may be addressed by id before any
    /// other write site has materialised the row.
    /// </summary>
    private async Task UpsertHumanEntityAsync(
        Action<HumanEntity> mutate,
        CancellationToken cancellationToken)
    {
        if (!GuidFormatter.TryParse(Id.GetId(), out var humanId))
        {
            throw new InvalidOperationException(
                $"Human actor id '{Id.GetId()}' is not a valid Guid; cannot persist EF state.");
        }

        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<SpringDbContext>();

        var entity = await db.Humans
            .FirstOrDefaultAsync(h => h.Id == humanId, cancellationToken);

        if (entity is null)
        {
            entity = new HumanEntity
            {
                Id = humanId,
                // Username is required (NOT NULL); the resolver normally
                // owns first-row creation. When the actor materialises the
                // row first (e.g. SetPermission before any login), seed
                // Username with the canonical id form so the unique index
                // is satisfied. The HumanIdentityResolver will overwrite
                // this with the real JWT username on the next login.
                Username = GuidFormatter.Format(humanId),
                DisplayName = GuidFormatter.Format(humanId),
            };
            mutate(entity);
            db.Humans.Add(entity);
        }
        else
        {
            mutate(entity);
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// Handles a status query message by returning the current permission level and identity.
    /// </summary>
    private async Task<Message?> HandleStatusQueryAsync(Message message, CancellationToken cancellationToken)
    {
        var entity = await LoadHumanEntityAsync(cancellationToken);
        var permission = entity?.PermissionLevel ?? PermissionLevel.Operator;
        var identity = entity?.Username ?? "unknown";

        var statusPayload = JsonSerializer.SerializeToElement(new
        {
            Permission = permission.ToString(),
            Identity = identity
        });

        return new Message(
            Guid.NewGuid(),
            Address,
            message.From,
            MessageType.StatusQuery,
            message.ThreadId,
            statusPayload,
            DateTimeOffset.UtcNow);
    }

    /// <summary>
    /// Handles a health check message by returning an acknowledgment indicating the actor is alive.
    /// </summary>
    private Message HandleHealthCheck(Message message)
    {
        var healthPayload = JsonSerializer.SerializeToElement(new { Healthy = true });

        return new Message(
            Guid.NewGuid(),
            Address,
            message.From,
            MessageType.HealthCheck,
            message.ThreadId,
            healthPayload,
            DateTimeOffset.UtcNow);
    }

    /// <summary>
    /// Handles a domain message by checking permission level.
    /// Viewers are rejected; Operators and Owners receive an acknowledgment.
    /// Notification channel routing is future work.
    /// </summary>
    private async Task<Message?> HandleDomainMessageAsync(Message message, CancellationToken cancellationToken)
    {
        var permission = await GetPermissionAsync(cancellationToken);

        if (permission == PermissionLevel.Viewer)
        {
            _logger.LogWarning("Human actor {ActorId} rejected domain message {MessageId}: insufficient permission (Viewer)",
                Id.GetId(), message.Id);
            return CreateErrorResponse(message, "Viewers cannot receive domain messages");
        }

        _logger.LogInformation("Human actor {ActorId} received domain message {MessageId}; notification routing is not yet implemented",
            Id.GetId(), message.Id);

        return CreateAckResponse(message);
    }

    /// <summary>
    /// Creates an acknowledgment response message.
    /// </summary>
    private Message CreateAckResponse(Message originalMessage)
    {
        var ackPayload = JsonSerializer.SerializeToElement(new { Acknowledged = true });
        return new Message(
            Guid.NewGuid(),
            Address,
            originalMessage.From,
            MessageType.Domain,
            originalMessage.ThreadId,
            ackPayload,
            DateTimeOffset.UtcNow);
    }

    /// <summary>
    /// Creates an error response message.
    /// </summary>
    private Message CreateErrorResponse(Message originalMessage, string errorMessage)
    {
        var errorPayload = JsonSerializer.SerializeToElement(new { Error = errorMessage });
        return new Message(
            Guid.NewGuid(),
            Address,
            originalMessage.From,
            MessageType.Domain,
            originalMessage.ThreadId,
            errorPayload,
            DateTimeOffset.UtcNow);
    }
}
