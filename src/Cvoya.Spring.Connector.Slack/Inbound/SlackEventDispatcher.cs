// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connector.Slack.Inbound;

using System.Text.Json;

using Cvoya.Spring.Connector.Slack.Auth.OAuth;
using Cvoya.Spring.Connector.Slack.Outbound;
using Cvoya.Spring.Connector.Slack.WebApi;
using Cvoya.Spring.Connectors;
using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Core.Secrets;
using Cvoya.Spring.Core.Tenancy;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

/// <summary>
/// Default <see cref="ISlackEventDispatcher"/>. Decodes the
/// <c>event_callback</c> envelope and runs the per-event-type
/// branches per ADR-0061 §2.2 / §2.4 / §3.
/// </summary>
public sealed class SlackEventDispatcher : ISlackEventDispatcher
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Refusal text the bot replies once with to unbound users per
    /// ADR-0061 §2.4.
    /// </summary>
    public const string UnboundUserRefusalText =
        "This Spring Voyage install is bound to one Slack user. You don't have access.";

    /// <summary>
    /// Auto-leave message the bot posts before leaving a channel it
    /// was invited to in <c>single_user_mode</c> per ADR-0061 §2.2.
    /// </summary>
    public const string AutoLeaveMessageText =
        "This Spring Voyage install is bound to one user and only operates in DM with that user. Leaving.";

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ISlackWebApiClient _webApi;
    private readonly ISlackThreadMapStore _threadMap;
    private readonly IUnboundUserRefusalGate _refusalGate;
    private readonly ISlackInboundAuditLog _auditLog;
    private readonly IMessageRouter _messageRouter;
    private readonly ILogger<SlackEventDispatcher> _logger;

    /// <summary>Creates a new <see cref="SlackEventDispatcher"/>.</summary>
    public SlackEventDispatcher(
        IServiceScopeFactory scopeFactory,
        ISlackWebApiClient webApi,
        ISlackThreadMapStore threadMap,
        IUnboundUserRefusalGate refusalGate,
        ISlackInboundAuditLog auditLog,
        IMessageRouter messageRouter,
        ILoggerFactory loggerFactory)
    {
        ArgumentNullException.ThrowIfNull(scopeFactory);
        _scopeFactory = scopeFactory;
        _webApi = webApi;
        _threadMap = threadMap;
        _refusalGate = refusalGate;
        _auditLog = auditLog;
        _messageRouter = messageRouter;
        _logger = loggerFactory.CreateLogger<SlackEventDispatcher>();
    }

    /// <inheritdoc />
    public async Task<SlackEventDispatchOutcome> DispatchAsync(
        JsonElement eventEnvelope,
        CancellationToken cancellationToken = default)
    {
        if (!eventEnvelope.TryGetProperty("team_id", out var teamIdProp)
            || teamIdProp.ValueKind != JsonValueKind.String)
        {
            return SlackEventDispatchOutcome.Ignored;
        }

        var teamId = teamIdProp.GetString();
        if (string.IsNullOrEmpty(teamId))
        {
            return SlackEventDispatchOutcome.Ignored;
        }

        // Resolve the tenant binding via the external_identity column
        // (the team_id), then load its TenantSlackConfig payload.
        TenantSlackConfig? config;
        await using (var lookupScope = _scopeFactory.CreateAsyncScope())
        {
            var bindingStore = lookupScope.ServiceProvider.GetRequiredService<ITenantConnectorBindingStore>();
            var binding = await bindingStore
                .GetByExternalIdentityAsync(SlackInstallStore.ConnectorSlug, teamId, cancellationToken)
                .ConfigureAwait(false);
            if (binding is null)
            {
                _logger.LogInformation(
                    "Slack inbound: no tenant binding for team_id={TeamId}; ignoring event.",
                    teamId);
                return SlackEventDispatchOutcome.UnknownTeam;
            }
            config = binding.Config.Deserialize<TenantSlackConfig>(JsonOptions);
            if (config is null)
            {
                _logger.LogWarning(
                    "Slack inbound: binding config payload was not TenantSlackConfig-shaped (team_id={TeamId}); ignoring.",
                    teamId);
                return SlackEventDispatchOutcome.UnknownTeam;
            }
        }

        if (!eventEnvelope.TryGetProperty("event", out var inner)
            || inner.ValueKind != JsonValueKind.Object)
        {
            return SlackEventDispatchOutcome.Ignored;
        }

        if (!inner.TryGetProperty("type", out var typeProp)
            || typeProp.ValueKind != JsonValueKind.String)
        {
            return SlackEventDispatchOutcome.Ignored;
        }

        var eventType = typeProp.GetString();
        return eventType switch
        {
            "member_joined_channel" => await HandleMemberJoinedAsync(teamId, config, inner, cancellationToken)
                .ConfigureAwait(false),
            "message" => await HandleMessageAsync(teamId, config, inner, cancellationToken)
                .ConfigureAwait(false),
            _ => SlackEventDispatchOutcome.Ignored,
        };
    }

    private async Task<SlackEventDispatchOutcome> HandleMemberJoinedAsync(
        string teamId,
        TenantSlackConfig config,
        JsonElement evt,
        CancellationToken cancellationToken)
    {
        // ADR-0061 §7.3: auto-leave is gated on single_user_mode, not
        // hardcoded. Multi-user installs flip the flag to false and
        // the connector subscribes to channel events normally.
        if (!config.SingleUserMode)
        {
            return SlackEventDispatchOutcome.Ignored;
        }

        // Only the bot's own join triggers the auto-leave.
        var joinedUser = evt.TryGetProperty("user", out var userProp)
            && userProp.ValueKind == JsonValueKind.String
            ? userProp.GetString()
            : null;
        if (string.IsNullOrEmpty(joinedUser)
            || !string.Equals(joinedUser, config.BotUserId, StringComparison.Ordinal))
        {
            return SlackEventDispatchOutcome.Ignored;
        }

        var channelId = evt.TryGetProperty("channel", out var channelProp)
            && channelProp.ValueKind == JsonValueKind.String
            ? channelProp.GetString()
            : null;
        if (string.IsNullOrEmpty(channelId))
        {
            return SlackEventDispatchOutcome.Ignored;
        }

        var botToken = await ReadBotTokenAsync(config, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrEmpty(botToken))
        {
            _logger.LogWarning(
                "Slack inbound: bot token not resolvable for team_id={TeamId}; auto-leave skipped.",
                teamId);
            return SlackEventDispatchOutcome.Handled;
        }

        await _webApi.PostMessageAsync(
            botToken,
            channelId,
            AutoLeaveMessageText,
            threadTs: null,
            username: null,
            iconUrl: null,
            cancellationToken).ConfigureAwait(false);

        var leave = await _webApi.ConversationsLeaveAsync(botToken, channelId, cancellationToken).ConfigureAwait(false);
        if (!leave.Ok)
        {
            _logger.LogWarning(
                "Slack inbound: conversations.leave returned ok=false (team_id={TeamId} channel={Channel} error={Error}).",
                teamId, channelId, leave.Error);
        }

        await _auditLog.RecordAsync(new SlackInboundAuditEvent(
            EventType: "member_joined_channel",
            TeamId: teamId,
            SlackUserId: config.BotUserId,
            ActingTenantUserId: null,
            Disposition: "auto-leave",
            Detail: channelId), cancellationToken).ConfigureAwait(false);

        return SlackEventDispatchOutcome.Handled;
    }

    private async Task<SlackEventDispatchOutcome> HandleMessageAsync(
        string teamId,
        TenantSlackConfig config,
        JsonElement evt,
        CancellationToken cancellationToken)
    {
        // Restrict to DMs per ADR-0061 §2.2. Slack tags DM messages
        // with channel_type == "im". Channel messages have other
        // types — we don't subscribe to them in v0.1 scope but we
        // defensively ignore them if delivered.
        var channelType = evt.TryGetProperty("channel_type", out var ct)
            && ct.ValueKind == JsonValueKind.String
            ? ct.GetString()
            : null;
        if (!string.Equals(channelType, "im", StringComparison.Ordinal))
        {
            return SlackEventDispatchOutcome.Ignored;
        }

        // Bot's own messages echo back as message events when the bot
        // posts via chat.postMessage. Skip them so we don't loop.
        if (evt.TryGetProperty("bot_id", out var botIdProp)
            && botIdProp.ValueKind == JsonValueKind.String
            && !string.IsNullOrEmpty(botIdProp.GetString()))
        {
            return SlackEventDispatchOutcome.Ignored;
        }

        var slackUserId = evt.TryGetProperty("user", out var userProp)
            && userProp.ValueKind == JsonValueKind.String
            ? userProp.GetString()
            : null;
        if (string.IsNullOrEmpty(slackUserId))
        {
            return SlackEventDispatchOutcome.Ignored;
        }

        var threadTs = evt.TryGetProperty("thread_ts", out var threadTsProp)
            && threadTsProp.ValueKind == JsonValueKind.String
            ? threadTsProp.GetString()
            : null;

        // Iterate the bound-user list — ADR-0061 §7.1: length 1 in
        // OSS but the code path must iterate.
        TenantBoundUser? matchedBound = null;
        await using (var scope = _scopeFactory.CreateAsyncScope())
        {
            var bindingStore = scope.ServiceProvider.GetRequiredService<ITenantConnectorBindingStore>();
            var boundUsers = await bindingStore
                .GetBoundUsersAsync(SlackInstallStore.ConnectorSlug, cancellationToken)
                .ConfigureAwait(false);
            foreach (var bound in boundUsers)
            {
                if (string.Equals(bound.ExternalUserId, slackUserId, StringComparison.Ordinal))
                {
                    matchedBound = bound;
                    break;
                }
            }
        }

        if (matchedBound is null)
        {
            await HandleUnboundUserAsync(teamId, config, slackUserId, evt, cancellationToken).ConfigureAwait(false);
            return SlackEventDispatchOutcome.Handled;
        }

        // The message is from the bound user. We need a Slack-side
        // thread_ts to resolve which SV thread it belongs to.
        if (string.IsNullOrEmpty(threadTs))
        {
            // ADR-0061 §3: the only way an SV thread is created from
            // inside Slack in v0.1 is the /sv-thread command.
            // A message without a thread_ts is therefore a top-level
            // message in the DM with no SV thread to route it to. We
            // drop with an audit entry.
            await _auditLog.RecordAsync(new SlackInboundAuditEvent(
                EventType: "message.im",
                TeamId: teamId,
                SlackUserId: slackUserId,
                ActingTenantUserId: matchedBound.TenantUserId,
                Disposition: "dropped:no-thread",
                Detail: "DM message arrived without thread_ts; v0.1 only routes inside an SV-thread Slack thread."),
                cancellationToken).ConfigureAwait(false);
            return SlackEventDispatchOutcome.Handled;
        }

        var mapping = await _threadMap
            .LookupSvThreadAsync(teamId, threadTs, cancellationToken)
            .ConfigureAwait(false);
        if (mapping is null)
        {
            // The thread_ts is on a Slack thread the bot did not
            // create. Could be a thread created by another bot or a
            // pre-existing user thread the bot is part of. Drop with
            // an audit entry — no SV thread to route to.
            await _auditLog.RecordAsync(new SlackInboundAuditEvent(
                EventType: "message.im",
                TeamId: teamId,
                SlackUserId: slackUserId,
                ActingTenantUserId: matchedBound.TenantUserId,
                Disposition: "dropped:unknown-thread_ts",
                Detail: threadTs), cancellationToken).ConfigureAwait(false);
            return SlackEventDispatchOutcome.Handled;
        }

        // Construct the ADR-0060 envelope. Message.From is resolved
        // via ITenantUserHumanResolver per ADR-0062 §3 — the resolver
        // picks the per-thread Hat (reply pin) when one exists, else
        // PrimaryHumanId. Route the resulting message via the
        // platform's IMessageRouter.
        Address fromAddress;
        await using (var resolverScope = _scopeFactory.CreateAsyncScope())
        {
            var resolver = resolverScope.ServiceProvider.GetRequiredService<ITenantUserHumanResolver>();
            fromAddress = await resolver.PickFromAsync(
                callerTenantUserId: matchedBound.TenantUserId,
                explicitFromHumanId: null,
                threadId: mapping.SvThreadId,
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }

        // Resolve the SV thread's canonical participant set. Per
        // ADR-0060 §2 (`sv.messaging.send` semantics), an inbound
        // message from one participant must reach every OTHER
        // participant on the thread — IMessageRouter does NOT fan out
        // for direct (human/agent/unit) recipient addresses; it only
        // expands `role://` scopes. So the connector materialises one
        // Message per non-sender participant, each with its own Id,
        // sharing From, ThreadId, Payload, and Timestamp so all rows
        // belong to the same logical inbound event (#2885).
        IReadOnlyList<Address> recipients;
        await using (var threadScope = _scopeFactory.CreateAsyncScope())
        {
            var threadRegistry = threadScope.ServiceProvider.GetRequiredService<IThreadRegistry>();
            var entry = await threadRegistry
                .ResolveAsync(mapping.SvThreadId.ToString("N"), cancellationToken)
                .ConfigureAwait(false);
            if (entry is null)
            {
                _logger.LogWarning(
                    "Slack inbound: SV thread {SvThreadId} mapping exists but registry resolve returned null; dropping.",
                    mapping.SvThreadId);
                await _auditLog.RecordAsync(new SlackInboundAuditEvent(
                    EventType: "message.im",
                    TeamId: teamId,
                    SlackUserId: slackUserId,
                    ActingTenantUserId: matchedBound.TenantUserId,
                    Disposition: "dropped:thread-not-in-registry",
                    Detail: mapping.SvThreadId.ToString("N")), cancellationToken).ConfigureAwait(false);
                return SlackEventDispatchOutcome.Handled;
            }

            recipients = entry.Participants
                .Where(p => p is not null
                    && !(string.Equals(p.Scheme, fromAddress.Scheme, StringComparison.Ordinal)
                        && p.Id == fromAddress.Id))
                .ToList();
        }

        if (recipients.Count == 0)
        {
            // A valid SV thread always carries at least one party
            // besides the sender; an empty recipient set means the
            // registry was seeded oddly (self-only) or the sender
            // resolution drifted from the participant canonicalisation.
            // Drop with audit rather than echoing back to self.
            await _auditLog.RecordAsync(new SlackInboundAuditEvent(
                EventType: "message.im",
                TeamId: teamId,
                SlackUserId: slackUserId,
                ActingTenantUserId: matchedBound.TenantUserId,
                Disposition: "dropped:no-recipients",
                Detail: mapping.SvThreadId.ToString("N")), cancellationToken).ConfigureAwait(false);
            return SlackEventDispatchOutcome.Handled;
        }

        var text = ExtractText(evt);
        var payload = JsonSerializer.SerializeToElement(new
        {
            source = "slack",
            text,
        }, JsonOptions);
        var sharedTimestamp = DateTimeOffset.UtcNow;
        var threadIdString = mapping.SvThreadId.ToString("N");

        // Aggregate fan-out outcomes into a single audit event per
        // inbound — the audit log captures one row per inbound, not
        // one per recipient (ADR-0060 §2).
        var failures = new List<string>(capacity: recipients.Count);
        foreach (var recipient in recipients)
        {
            var perRecipientMessage = new Message(
                Id: Guid.NewGuid(),
                From: fromAddress,
                To: recipient,
                Type: MessageType.Domain,
                ThreadId: threadIdString,
                Payload: payload,
                Timestamp: sharedTimestamp);

            var routeResult = await _messageRouter
                .RouteAsync(perRecipientMessage, cancellationToken)
                .ConfigureAwait(false);
            if (!routeResult.IsSuccess)
            {
                failures.Add($"{recipient}:{routeResult.Error?.Message}");
            }
        }

        var disposition = failures.Count == 0
            ? "forwarded"
            : $"route-failed:{string.Join(';', failures)}";

        await _auditLog.RecordAsync(new SlackInboundAuditEvent(
            EventType: "message.im",
            TeamId: teamId,
            SlackUserId: slackUserId,
            ActingTenantUserId: matchedBound.TenantUserId,
            Disposition: disposition,
            Detail: threadIdString), cancellationToken).ConfigureAwait(false);

        return SlackEventDispatchOutcome.Handled;
    }

    private async Task HandleUnboundUserAsync(
        string teamId,
        TenantSlackConfig config,
        string slackUserId,
        JsonElement evt,
        CancellationToken cancellationToken)
    {
        // Once-per-session refusal per ADR-0061 §2.4.
        if (!_refusalGate.TryClaimRefusal(teamId, slackUserId))
        {
            await _auditLog.RecordAsync(new SlackInboundAuditEvent(
                EventType: "message.im",
                TeamId: teamId,
                SlackUserId: slackUserId,
                ActingTenantUserId: null,
                Disposition: "ignored:unbound-already-refused"),
                cancellationToken).ConfigureAwait(false);
            return;
        }

        var channel = evt.TryGetProperty("channel", out var channelProp)
            && channelProp.ValueKind == JsonValueKind.String
            ? channelProp.GetString()
            : null;
        if (string.IsNullOrEmpty(channel))
        {
            return;
        }

        var botToken = await ReadBotTokenAsync(config, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrEmpty(botToken))
        {
            _logger.LogWarning(
                "Slack inbound: bot token not resolvable for team_id={TeamId}; refusal skipped.",
                teamId);
            return;
        }

        await _webApi.PostMessageAsync(
            botToken,
            channel,
            EventDispatcherUnboundUserText(),
            threadTs: null,
            username: null,
            iconUrl: null,
            cancellationToken).ConfigureAwait(false);

        await _auditLog.RecordAsync(new SlackInboundAuditEvent(
            EventType: "message.im",
            TeamId: teamId,
            SlackUserId: slackUserId,
            ActingTenantUserId: null,
            Disposition: "refused:unbound"), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The refusal text exposed as a method so tests can compare
    /// against the constant verbatim.
    /// </summary>
    internal static string EventDispatcherUnboundUserText() => UnboundUserRefusalText;

    private async Task<string?> ReadBotTokenAsync(TenantSlackConfig config, CancellationToken cancellationToken)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var resolver = scope.ServiceProvider.GetRequiredService<ISecretResolver>();
        var tenantContext = scope.ServiceProvider.GetRequiredService<ITenantContext>();

        var resolution = await resolver
            .ResolveWithPathAsync(
                new SecretRef(SecretScope.Tenant, tenantContext.CurrentTenantId, config.BotTokenSecretName),
                cancellationToken)
            .ConfigureAwait(false);

        return resolution.Value;
    }

    // One of several payload-text extractors across the codebase; the
    // canonical-field-vs-renderer-registry decision is tracked in #2843.
    private static string ExtractText(JsonElement evt)
    {
        if (evt.TryGetProperty("text", out var text)
            && text.ValueKind == JsonValueKind.String)
        {
            return text.GetString() ?? string.Empty;
        }
        return string.Empty;
    }
}
