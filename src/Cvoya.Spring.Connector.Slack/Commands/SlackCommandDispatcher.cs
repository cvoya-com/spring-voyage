// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connector.Slack.Commands;

using System.Collections.Generic;
using System.Text.Json;

using Cvoya.Spring.Connector.Slack.Auth.OAuth;
using Cvoya.Spring.Connector.Slack.Inbound;
using Cvoya.Spring.Connector.Slack.Outbound;
using Cvoya.Spring.Connector.Slack.Slug;
using Cvoya.Spring.Connector.Slack.WebApi;
using Cvoya.Spring.Connectors;
using Cvoya.Spring.Core.Directory;
using Cvoya.Spring.Core.Identifiers;
using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Core.Secrets;
using Cvoya.Spring.Core.Tenancy;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

/// <summary>
/// Default <see cref="ISlackCommandDispatcher"/>. Implements ADR-0061
/// §5 — the three slash commands plus the Block Kit interaction
/// surface that backs the <c>/sv-thread</c> modal submission.
/// </summary>
public sealed class SlackCommandDispatcher : ISlackCommandDispatcher
{
    /// <summary>Slash-command slug for "open the start-a-thread modal".</summary>
    public const string SvThreadCommand = "/sv-thread";

    /// <summary>Slash-command slug for "list active SV threads".</summary>
    public const string SvThreadsCommand = "/sv-threads";

    /// <summary>Slash-command slug for "show the cheat sheet".</summary>
    public const string SvHelpCommand = "/sv-help";

    /// <summary>Modal callback id consumed by <c>view_submission</c>.</summary>
    public const string SvThreadModalCallbackId = "sv_thread_modal";

    /// <summary>Block id of the multi-select in the /sv-thread modal.</summary>
    public const string SvThreadParticipantsBlockId = "sv_thread_participants";

    /// <summary>Block id of the optional initial-message field.</summary>
    public const string SvThreadInitialMessageBlockId = "sv_thread_initial_message";

    /// <summary>Action id of the participant multi-select.</summary>
    public const string SvThreadParticipantsActionId = "sv_thread_participants_select";

    /// <summary>Action id of the initial-message text field.</summary>
    public const string SvThreadInitialMessageActionId = "sv_thread_initial_message_input";

    /// <summary>
    /// Wall-clock budget for resolving Slack canonical permalinks
    /// during <c>/sv-threads</c> render. Slack's slash-command
    /// handshake is 3 seconds; the cap leaves room for the subsequent
    /// <c>views.open</c> call. Per-row failures (Slack <c>ok=false</c>,
    /// transport exception, or budget exceeded) fall back to the
    /// workspace-local <c>slack://</c> URI for that row.
    /// </summary>
    internal const int PermalinkBudgetMs = 1500;

    /// <summary>
    /// Static cheat-sheet text the bot posts in the DM in response to
    /// <c>/sv-help</c>.
    /// </summary>
    public const string CheatSheetText =
        "*Spring Voyage Slack cheat sheet*\n" +
        "• `/sv-thread` — start a new SV thread with one or more agents, units, or humans.\n" +
        "• `/sv-threads` — list your active SV threads with deep links to each.\n" +
        "• `/sv-help` — show this message.\n\n" +
        "All commands operate in this DM with the SV bot only.";

    /// <summary>
    /// DM-only refusal message for commands invoked outside the bot
    /// DM. Same shape as the unbound-user refusal per ADR-0061 §5.
    /// </summary>
    public const string DmOnlyRefusalText = SlackEventDispatcher.UnboundUserRefusalText;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ISlackWebApiClient _webApi;
    private readonly ISlackThreadMapStore _threadMap;
    private readonly ISlackThreadSlugBuilder _slugBuilder;
    private readonly ISlackInboundAuditLog _auditLog;
    private readonly IMessageRouter _messageRouter;
    private readonly ILogger<SlackCommandDispatcher> _logger;

    /// <summary>Creates a new <see cref="SlackCommandDispatcher"/>.</summary>
    public SlackCommandDispatcher(
        IServiceScopeFactory scopeFactory,
        ISlackWebApiClient webApi,
        ISlackThreadMapStore threadMap,
        ISlackThreadSlugBuilder slugBuilder,
        ISlackInboundAuditLog auditLog,
        IMessageRouter messageRouter,
        ILoggerFactory loggerFactory)
    {
        ArgumentNullException.ThrowIfNull(scopeFactory);
        _scopeFactory = scopeFactory;
        _webApi = webApi;
        _threadMap = threadMap;
        _slugBuilder = slugBuilder;
        _auditLog = auditLog;
        _messageRouter = messageRouter;
        _logger = loggerFactory.CreateLogger<SlackCommandDispatcher>();
    }

    /// <inheritdoc />
    public async Task<SlackCommandDispatchOutcome> DispatchAsync(
        IReadOnlyDictionary<string, string> form,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(form);

        var commandSlug = GetForm(form, "command");
        var teamId = GetForm(form, "team_id");
        var userId = GetForm(form, "user_id");
        var triggerId = GetForm(form, "trigger_id");
        var channelName = GetForm(form, "channel_name");
        var channelId = GetForm(form, "channel_id");
        var initialText = GetForm(form, "text");

        if (string.IsNullOrEmpty(commandSlug)
            || (!string.Equals(commandSlug, SvThreadCommand, StringComparison.Ordinal)
                && !string.Equals(commandSlug, SvThreadsCommand, StringComparison.Ordinal)
                && !string.Equals(commandSlug, SvHelpCommand, StringComparison.Ordinal)))
        {
            return SlackCommandDispatchOutcome.UnknownCommand;
        }

        // ADR-0061 §5: commands operate in the bound user's bot DM
        // only. Slack tags DM channel names with the literal
        // "directmessage"; channels carry their human-readable name.
        if (!string.Equals(channelName, "directmessage", StringComparison.Ordinal))
        {
            // Slack does not let us post into an arbitrary channel
            // without invitation. The refusal text is surfaced
            // ephemerally via the response payload Slack handles
            // from the slash-command response body.
            await RespondEphemerallyAsync(channelId, triggerId, DmOnlyRefusalText, cancellationToken).ConfigureAwait(false);
            await _auditLog.RecordAsync(new SlackInboundAuditEvent(
                EventType: $"command:{commandSlug}",
                TeamId: teamId,
                SlackUserId: userId,
                ActingTenantUserId: null,
                Disposition: "refused:non-dm",
                Detail: channelName), cancellationToken).ConfigureAwait(false);
            return SlackCommandDispatchOutcome.Handled;
        }

        var (binding, config) = await LoadBindingByTeamAsync(teamId, cancellationToken).ConfigureAwait(false);
        if (binding is null || config is null)
        {
            return SlackCommandDispatchOutcome.UnknownTeam;
        }

        // Identify the calling bound user (the v0.1 list has length
        // 1 in OSS, but the code path iterates per ADR-0061 §7.1).
        var matchedBound = await ResolveBoundUserAsync(teamId, userId, cancellationToken).ConfigureAwait(false);
        if (matchedBound is null)
        {
            // Unbound user invoked a /sv-* command from inside the
            // bot DM. Same refusal text.
            await RespondEphemerallyAsync(channelId, triggerId, DmOnlyRefusalText, cancellationToken).ConfigureAwait(false);
            await _auditLog.RecordAsync(new SlackInboundAuditEvent(
                EventType: $"command:{commandSlug}",
                TeamId: teamId,
                SlackUserId: userId,
                ActingTenantUserId: null,
                Disposition: "refused:unbound"), cancellationToken).ConfigureAwait(false);
            return SlackCommandDispatchOutcome.Handled;
        }

        var botToken = await ReadBotTokenAsync(config, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrEmpty(botToken))
        {
            return SlackCommandDispatchOutcome.UnknownTeam;
        }

        switch (commandSlug)
        {
            case SvThreadCommand:
                await OpenSvThreadModalAsync(botToken, triggerId, matchedBound.TenantUserId, initialText, cancellationToken)
                    .ConfigureAwait(false);
                break;
            case SvThreadsCommand:
                await OpenSvThreadsModalAsync(botToken, triggerId, matchedBound.TenantUserId, config.TeamId, cancellationToken)
                    .ConfigureAwait(false);
                break;
            case SvHelpCommand:
                await _webApi.PostMessageAsync(
                    botToken,
                    channelId,
                    CheatSheetText,
                    threadTs: null,
                    username: null,
                    iconUrl: null,
                    cancellationToken).ConfigureAwait(false);
                break;
        }

        await _auditLog.RecordAsync(new SlackInboundAuditEvent(
            EventType: $"command:{commandSlug}",
            TeamId: teamId,
            SlackUserId: userId,
            ActingTenantUserId: matchedBound.TenantUserId,
            Disposition: "handled"), cancellationToken).ConfigureAwait(false);

        return SlackCommandDispatchOutcome.Handled;
    }

    /// <summary>
    /// Audit-event type stamped on every <c>view_submission</c> the
    /// <c>/sv-thread</c> modal generates — sync validation outcomes
    /// (refused, validation-failed) and background-work outcomes
    /// (thread-created, failed) all share this prefix.
    /// </summary>
    internal const string ViewSubmissionEventType = "view_submission:" + SvThreadModalCallbackId;

    /// <summary>
    /// User-facing copy posted in the bot DM when the background
    /// thread-creation task throws an unhandled exception. Kept short
    /// so the operator sees something actionable in the DM without
    /// inviting them to copy/paste error text — the audit log carries
    /// the structured detail.
    /// </summary>
    internal const string BackgroundFailureDmText =
        ":warning: Couldn't start that SV thread — please try again. "
        + "If it keeps failing, check the server logs.";

    /// <summary>
    /// Test-only handle to the most recently scheduled background
    /// thread-creation task. Production code never awaits this —
    /// the request thread returns as soon as the work is enqueued.
    /// Tests in <c>SlackCommandDispatcherTests</c> await it to assert
    /// on observable post-conditions (web-API calls, audit records).
    /// </summary>
    internal Task LastBackgroundTask { get; private set; } = Task.CompletedTask;

    /// <inheritdoc />
    public async Task<SlackInteractionResponse> DispatchInteractionAsync(
        JsonElement payload,
        CancellationToken cancellationToken = default)
    {
        if (!payload.TryGetProperty("type", out var typeProp)
            || typeProp.ValueKind != JsonValueKind.String)
        {
            return new SlackInteractionResponse(SlackCommandDispatchOutcome.UnknownCommand);
        }

        // We only handle view_submission in v0.1. block_actions and
        // similar are accepted (200) but no-op.
        if (!string.Equals(typeProp.GetString(), "view_submission", StringComparison.Ordinal))
        {
            return new SlackInteractionResponse(SlackCommandDispatchOutcome.Handled);
        }

        if (!payload.TryGetProperty("view", out var view)
            || view.ValueKind != JsonValueKind.Object)
        {
            return new SlackInteractionResponse(SlackCommandDispatchOutcome.UnknownCommand);
        }

        var callbackId = view.TryGetProperty("callback_id", out var cb)
            && cb.ValueKind == JsonValueKind.String
            ? cb.GetString()
            : null;
        if (!string.Equals(callbackId, SvThreadModalCallbackId, StringComparison.Ordinal))
        {
            return new SlackInteractionResponse(SlackCommandDispatchOutcome.Handled);
        }

        var teamId = payload.TryGetProperty("team", out var team)
            && team.ValueKind == JsonValueKind.Object
            && team.TryGetProperty("id", out var teamIdProp)
            && teamIdProp.ValueKind == JsonValueKind.String
            ? teamIdProp.GetString() ?? string.Empty
            : string.Empty;

        var userId = payload.TryGetProperty("user", out var user)
            && user.ValueKind == JsonValueKind.Object
            && user.TryGetProperty("id", out var userIdProp)
            && userIdProp.ValueKind == JsonValueKind.String
            ? userIdProp.GetString() ?? string.Empty
            : string.Empty;

        var (binding, config) = await LoadBindingByTeamAsync(teamId, cancellationToken).ConfigureAwait(false);
        if (binding is null || config is null)
        {
            return new SlackInteractionResponse(SlackCommandDispatchOutcome.UnknownTeam);
        }

        var matchedBound = await ResolveBoundUserAsync(teamId, userId, cancellationToken).ConfigureAwait(false);
        if (matchedBound is null)
        {
            return new SlackInteractionResponse(SlackCommandDispatchOutcome.UnknownTeam);
        }

        // Parse the form values out of the view state.
        var state = view.GetProperty("state").GetProperty("values");

        var selectedAddresses = ParseSelectedParticipants(state);
        var initialMessage = ParseInitialMessage(state);

        // Inline-validation failure: return response_action=errors so
        // Slack keeps the modal open with the field error highlighted
        // instead of closing silently. Defence-in-depth against a
        // hand-rolled payload — Slack's own client also enforces this
        // because the participants input block is not marked optional.
        if (selectedAddresses.Count == 0)
        {
            await _auditLog.RecordAsync(new SlackInboundAuditEvent(
                EventType: ViewSubmissionEventType,
                TeamId: teamId,
                SlackUserId: userId,
                ActingTenantUserId: matchedBound.TenantUserId,
                Disposition: "validation-failed",
                Detail: "no-participants"), cancellationToken).ConfigureAwait(false);

            return new SlackInteractionResponse(
                SlackCommandDispatchOutcome.Handled,
                ResponseBody: new
                {
                    response_action = "errors",
                    errors = new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        [SvThreadParticipantsBlockId] = "Pick at least one participant.",
                    },
                });
        }

        var botToken = await ReadBotTokenAsync(config, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrEmpty(botToken))
        {
            return new SlackInteractionResponse(SlackCommandDispatchOutcome.UnknownTeam);
        }

        // #2879: Slack's view_submission ack budget is 3 seconds; the
        // synchronous thread-creation path (DM open + chat.postMessage
        // + EF write + optional message route) was observed at 17.5s
        // on a clean install. Run that work on a background task so
        // the modal closes cleanly, and use CancellationToken.None so
        // the request token's cancellation (fired as soon as the HTTP
        // response completes) does not abort the in-flight work.
        var backgroundTask = Task.Run(
            () => ExecuteThreadCreationAsync(
                teamId,
                userId,
                matchedBound,
                config,
                botToken,
                selectedAddresses,
                initialMessage,
                CancellationToken.None),
            CancellationToken.None);

        LastBackgroundTask = backgroundTask;

        return new SlackInteractionResponse(SlackCommandDispatchOutcome.Handled);
    }

    /// <summary>
    /// Background companion to <see cref="DispatchInteractionAsync"/>.
    /// Runs after the HTTP ack so the modal closes within Slack's
    /// 3-second budget (#2879). All failures are caught, logged,
    /// audited, and best-effort surfaced back to the user as a DM —
    /// never bubbled, because there is no caller to surface them to.
    /// </summary>
    private async Task ExecuteThreadCreationAsync(
        string teamId,
        string userId,
        TenantBoundUser matchedBound,
        TenantSlackConfig config,
        string botToken,
        IReadOnlyList<Address> selectedAddresses,
        string? initialMessage,
        CancellationToken cancellationToken)
    {
        try
        {
            // Resolve the bound user's primary Hat via the resolver.
            Address fromAddress;
            await using (var resolverScope = _scopeFactory.CreateAsyncScope())
            {
                var resolver = resolverScope.ServiceProvider.GetRequiredService<ITenantUserHumanResolver>();
                fromAddress = await resolver.PickFromAsync(
                    callerTenantUserId: matchedBound.TenantUserId,
                    explicitFromHumanId: null,
                    threadId: null,
                    cancellationToken: cancellationToken).ConfigureAwait(false);
            }

            // Resolve / create the SV thread.
            var participantSet = new List<Address> { fromAddress };
            participantSet.AddRange(selectedAddresses);

            Guid svThreadId;
            await using (var threadScope = _scopeFactory.CreateAsyncScope())
            {
                var registry = threadScope.ServiceProvider.GetRequiredService<IThreadRegistry>();
                var threadIdHex = await registry.GetOrCreateAsync(participantSet, cancellationToken).ConfigureAwait(false);
                if (!Guid.TryParse(threadIdHex, out svThreadId) && !GuidFormatter.TryParse(threadIdHex, out svThreadId))
                {
                    await _auditLog.RecordAsync(new SlackInboundAuditEvent(
                        EventType: ViewSubmissionEventType,
                        TeamId: teamId,
                        SlackUserId: userId,
                        ActingTenantUserId: matchedBound.TenantUserId,
                        Disposition: "failed",
                        Detail: "thread-id-unparseable:" + threadIdHex), cancellationToken).ConfigureAwait(false);
                    return;
                }
            }

            // Open the bot DM with the bound Slack user and post the
            // parent message + record the thread_ts. Match the outbound-
            // dispatcher path.
            var openResult = await _webApi.OpenConversationAsync(botToken, matchedBound.ExternalUserId, cancellationToken)
                .ConfigureAwait(false);
            if (!openResult.Ok || string.IsNullOrEmpty(openResult.ChannelId))
            {
                await _auditLog.RecordAsync(new SlackInboundAuditEvent(
                    EventType: ViewSubmissionEventType,
                    TeamId: teamId,
                    SlackUserId: userId,
                    ActingTenantUserId: matchedBound.TenantUserId,
                    Disposition: "failed",
                    Detail: "conversations.open:" + (openResult.Error ?? "no-channel")), cancellationToken).ConfigureAwait(false);
                return;
            }

            var slug = await _slugBuilder.BuildSlugAsync(
                participantSet,
                matchedBound.TenantUserId,
                svThreadId,
                cancellationToken).ConfigureAwait(false);

            var parent = await _webApi.PostMessageAsync(
                botToken,
                openResult.ChannelId,
                slug,
                threadTs: null,
                username: null,
                iconUrl: null,
                cancellationToken).ConfigureAwait(false);
            if (!parent.Ok)
            {
                await _auditLog.RecordAsync(new SlackInboundAuditEvent(
                    EventType: ViewSubmissionEventType,
                    TeamId: teamId,
                    SlackUserId: userId,
                    ActingTenantUserId: matchedBound.TenantUserId,
                    Disposition: "failed",
                    Detail: "chat.postMessage:" + (parent.Error ?? "unknown")), cancellationToken).ConfigureAwait(false);
                await TryPostFailureDmAsync(botToken, openResult.ChannelId, cancellationToken).ConfigureAwait(false);
                return;
            }

            await _threadMap.RecordAsync(
                svThreadId,
                matchedBound.TenantUserId,
                config.TeamId,
                parent.ChannelId,
                parent.MessageTs,
                cancellationToken).ConfigureAwait(false);

            // Optional initial-message: route it through the platform's
            // IMessageRouter so the outbound delivery path (Part A) picks
            // it up. The router will fan out to recipient actors; the
            // Slack outbound dispatcher then echoes them into the
            // bot DM thread alongside any agent replies.
            if (!string.IsNullOrWhiteSpace(initialMessage))
            {
                var firstRecipient = selectedAddresses[0];
                var msg = new Message(
                    Id: Guid.NewGuid(),
                    From: fromAddress,
                    To: firstRecipient,
                    Type: MessageType.Domain,
                    ThreadId: GuidFormatter.Format(svThreadId),
                    Payload: JsonSerializer.SerializeToElement(new
                    {
                        source = "slack",
                        text = initialMessage,
                    }, JsonOptions),
                    Timestamp: DateTimeOffset.UtcNow);

                await _messageRouter.RouteAsync(msg, cancellationToken).ConfigureAwait(false);
            }

            await _auditLog.RecordAsync(new SlackInboundAuditEvent(
                EventType: ViewSubmissionEventType,
                TeamId: teamId,
                SlackUserId: userId,
                ActingTenantUserId: matchedBound.TenantUserId,
                Disposition: "thread-created",
                Detail: GuidFormatter.Format(svThreadId)), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Slack /sv-thread modal: background thread creation failed for tenantUser={TenantUserId} team={TeamId}.",
                matchedBound.TenantUserId,
                teamId);

            try
            {
                await _auditLog.RecordAsync(new SlackInboundAuditEvent(
                    EventType: ViewSubmissionEventType,
                    TeamId: teamId,
                    SlackUserId: userId,
                    ActingTenantUserId: matchedBound.TenantUserId,
                    Disposition: "failed",
                    Detail: ex.GetType().Name + ": " + ex.Message), cancellationToken).ConfigureAwait(false);
            }
            catch (Exception auditEx)
            {
                _logger.LogWarning(auditEx, "Slack /sv-thread modal: failed to record audit event for background failure.");
            }

            try
            {
                var openResult = await _webApi.OpenConversationAsync(botToken, matchedBound.ExternalUserId, cancellationToken)
                    .ConfigureAwait(false);
                if (openResult.Ok && !string.IsNullOrEmpty(openResult.ChannelId))
                {
                    await TryPostFailureDmAsync(botToken, openResult.ChannelId, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (Exception dmEx)
            {
                _logger.LogWarning(dmEx, "Slack /sv-thread modal: failed to DM operator about background failure.");
            }
        }
    }

    private async Task TryPostFailureDmAsync(string botToken, string channelId, CancellationToken cancellationToken)
    {
        try
        {
            await _webApi.PostMessageAsync(
                botToken,
                channelId,
                BackgroundFailureDmText,
                threadTs: null,
                username: null,
                iconUrl: null,
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Slack /sv-thread modal: failed to post background-failure DM.");
        }
    }

    private async Task OpenSvThreadModalAsync(
        string botToken,
        string triggerId,
        Guid boundTenantUserId,
        string? initialText,
        CancellationToken cancellationToken)
    {
        // Pull every directory entry; filter out the bound user's
        // own Hat to avoid the user picking themselves.
        IReadOnlyList<DirectoryEntry> entries;
        await using (var dirScope = _scopeFactory.CreateAsyncScope())
        {
            var dir = dirScope.ServiceProvider.GetRequiredService<IDirectoryService>();
            entries = await dir.ListAllAsync(cancellationToken).ConfigureAwait(false);
        }

        // Resolve the bound user's primary Hat so we can exclude it.
        Address? primaryHat = null;
        try
        {
            await using var resolverScope = _scopeFactory.CreateAsyncScope();
            var resolver = resolverScope.ServiceProvider.GetRequiredService<ITenantUserHumanResolver>();
            primaryHat = await resolver.PickFromAsync(
                callerTenantUserId: boundTenantUserId,
                explicitFromHumanId: null,
                threadId: null,
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (NoBoundHumanException)
        {
            // Nothing pinned; show the full directory.
        }

        var options = entries
            .Where(e => e.Address is { } addr
                && (string.Equals(addr.Scheme, Address.AgentScheme, StringComparison.Ordinal)
                    || string.Equals(addr.Scheme, Address.UnitScheme, StringComparison.Ordinal)
                    || string.Equals(addr.Scheme, Address.HumanScheme, StringComparison.Ordinal)))
            .Where(e => primaryHat is null
                || !(string.Equals(e.Address.Scheme, primaryHat.Scheme, StringComparison.Ordinal)
                    && e.Address.Id == primaryHat.Id))
            .OrderBy(e => e.DisplayName, StringComparer.OrdinalIgnoreCase)
            .Take(100) // Slack caps option lists at ~100
            .Select(e => new
            {
                text = new { type = "plain_text", text = e.DisplayName },
                value = e.Address.ToString(),
            })
            .Cast<object>()
            .ToList();

        var view = BuildSvThreadModalView(options, initialText);
        await _webApi.ViewsOpenAsync(botToken, triggerId, view, cancellationToken).ConfigureAwait(false);
    }

    private async Task OpenSvThreadsModalAsync(
        string botToken,
        string triggerId,
        Guid boundTenantUserId,
        string teamId,
        CancellationToken cancellationToken)
    {
        var mappings = await _threadMap
            .ListForBoundUserAsync(boundTenantUserId, teamId, cancellationToken)
            .ConfigureAwait(false);

        var blocks = new List<object>
        {
            new
            {
                type = "header",
                text = new { type = "plain_text", text = "Your SV threads" },
            },
        };

        if (mappings.Count == 0)
        {
            blocks.Add(new
            {
                type = "section",
                text = new
                {
                    type = "mrkdwn",
                    text = "_No active SV threads in this workspace yet. Use `/sv-thread` to start one._",
                },
            });
        }
        else
        {
            // ADR-0061 §5: resolve the canonical HTTPS permalink per
            // row so the deep link survives cross-workspace and non-
            // Slack-client contexts. Fetches run in parallel under a
            // shared budget; on per-row failure (Slack ok=false,
            // transport error, or budget exceeded) we fall back to
            // the workspace-local slack:// URI for that row.
            var permalinks = await ResolvePermalinksAsync(botToken, mappings, cancellationToken)
                .ConfigureAwait(false);

            foreach (var mapping in mappings)
            {
                var key = (mapping.SlackChannelId, mapping.SlackThreadTs);
                var deepLink = permalinks.TryGetValue(key, out var permalink)
                    && !string.IsNullOrEmpty(permalink)
                    ? permalink
                    : BuildSlackDeepLinkFallback(mapping);

                blocks.Add(new
                {
                    type = "section",
                    text = new
                    {
                        type = "mrkdwn",
                        text = $"<{deepLink}|SV thread {GuidFormatter.Format(mapping.SvThreadId)}>",
                    },
                });
            }
        }

        var viewObj = new
        {
            type = "modal",
            callback_id = "sv_threads_modal",
            title = new { type = "plain_text", text = "SV threads" },
            close = new { type = "plain_text", text = "Close" },
            blocks,
        };

        var view = JsonSerializer.SerializeToElement(viewObj, JsonOptions);
        await _webApi.ViewsOpenAsync(botToken, triggerId, view, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Resolves Slack canonical permalinks for every unique
    /// <c>(channel, thread_ts)</c> pair in <paramref name="mappings"/>
    /// in parallel, capped by <see cref="PermalinkBudgetMs"/>. Permalinks
    /// are deduped per (channel, thread_ts) so repeated rows pointing
    /// at the same Slack message only trigger one fetch — the
    /// "cache for the duration of the modal render" called out in
    /// ADR-0061 §5.
    /// </summary>
    private async Task<IReadOnlyDictionary<(string Channel, string ThreadTs), string>> ResolvePermalinksAsync(
        string botToken,
        IReadOnlyList<SlackThreadMapping> mappings,
        CancellationToken cancellationToken)
    {
        var distinctKeys = mappings
            .Select(m => (m.SlackChannelId, m.SlackThreadTs))
            .Distinct()
            .ToList();

        using var budgetCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budgetCts.CancelAfter(TimeSpan.FromMilliseconds(PermalinkBudgetMs));

        var tasks = distinctKeys.ToDictionary(
            key => key,
            key => TryGetPermalinkAsync(botToken, key.SlackChannelId, key.SlackThreadTs, budgetCts.Token));

        await Task.WhenAll(tasks.Values).ConfigureAwait(false);

        var result = new Dictionary<(string Channel, string ThreadTs), string>(tasks.Count);
        foreach (var (key, task) in tasks)
        {
            if (task.Result is { Length: > 0 } permalink)
            {
                result[key] = permalink;
            }
        }
        return result;
    }

    private async Task<string?> TryGetPermalinkAsync(
        string botToken,
        string channelId,
        string messageTs,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await _webApi
                .GetPermalinkAsync(botToken, channelId, messageTs, cancellationToken)
                .ConfigureAwait(false);

            if (result.Ok && !string.IsNullOrEmpty(result.Permalink))
            {
                return result.Permalink;
            }

            _logger.LogInformation(
                "Slack chat.getPermalink returned ok=false for channel={Channel} ts={Ts}; falling back to slack:// URI. Error={Error}",
                channelId, messageTs, result.Error);
            return null;
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation(
                "Slack chat.getPermalink exceeded budget or was cancelled for channel={Channel} ts={Ts}; falling back to slack:// URI.",
                channelId, messageTs);
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Slack chat.getPermalink threw for channel={Channel} ts={Ts}; falling back to slack:// URI.",
                channelId, messageTs);
            return null;
        }
    }

    private static string BuildSlackDeepLinkFallback(SlackThreadMapping mapping) =>
        $"slack://channel?team={Uri.EscapeDataString(mapping.TeamId)}"
        + $"&id={Uri.EscapeDataString(mapping.SlackChannelId)}"
        + $"&message={Uri.EscapeDataString(mapping.SlackThreadTs)}";

    internal static JsonElement BuildSvThreadModalView(IReadOnlyList<object> options, string? initialText)
    {
        var participantsBlock = new
        {
            type = "input",
            block_id = SvThreadParticipantsBlockId,
            label = new { type = "plain_text", text = "Participants" },
            element = new
            {
                type = "multi_static_select",
                action_id = SvThreadParticipantsActionId,
                placeholder = new { type = "plain_text", text = "Pick agents, units, or humans" },
                options,
            },
        };

        var initialMessageBlock = new
        {
            type = "input",
            block_id = SvThreadInitialMessageBlockId,
            label = new { type = "plain_text", text = "Initial message (optional)" },
            optional = true,
            element = new
            {
                type = "plain_text_input",
                action_id = SvThreadInitialMessageActionId,
                multiline = true,
                initial_value = string.IsNullOrEmpty(initialText) ? null : initialText,
            },
        };

        var viewObj = new
        {
            type = "modal",
            callback_id = SvThreadModalCallbackId,
            title = new { type = "plain_text", text = "Start an SV thread" },
            submit = new { type = "plain_text", text = "Create" },
            close = new { type = "plain_text", text = "Cancel" },
            blocks = new object[] { participantsBlock, initialMessageBlock },
        };

        return JsonSerializer.SerializeToElement(viewObj, JsonOptions);
    }

    private static IReadOnlyList<Address> ParseSelectedParticipants(JsonElement state)
    {
        var result = new List<Address>();
        if (!state.TryGetProperty(SvThreadParticipantsBlockId, out var block)
            || block.ValueKind != JsonValueKind.Object)
        {
            return result;
        }
        if (!block.TryGetProperty(SvThreadParticipantsActionId, out var action)
            || action.ValueKind != JsonValueKind.Object)
        {
            return result;
        }
        if (!action.TryGetProperty("selected_options", out var selected)
            || selected.ValueKind != JsonValueKind.Array)
        {
            return result;
        }

        foreach (var element in selected.EnumerateArray())
        {
            if (element.ValueKind != JsonValueKind.Object) continue;
            if (!element.TryGetProperty("value", out var valueProp) || valueProp.ValueKind != JsonValueKind.String) continue;
            var value = valueProp.GetString();
            if (string.IsNullOrEmpty(value)) continue;
            if (Address.TryParse(value, out var addr) && addr is not null)
            {
                result.Add(addr);
            }
        }

        return result;
    }

    private static string? ParseInitialMessage(JsonElement state)
    {
        if (!state.TryGetProperty(SvThreadInitialMessageBlockId, out var block)
            || block.ValueKind != JsonValueKind.Object)
        {
            return null;
        }
        if (!block.TryGetProperty(SvThreadInitialMessageActionId, out var action)
            || action.ValueKind != JsonValueKind.Object)
        {
            return null;
        }
        if (!action.TryGetProperty("value", out var valueProp))
        {
            return null;
        }
        return valueProp.ValueKind switch
        {
            JsonValueKind.String => valueProp.GetString(),
            _ => null,
        };
    }

    private async Task<(TenantConnectorBinding? Binding, TenantSlackConfig? Config)> LoadBindingByTeamAsync(
        string teamId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(teamId))
        {
            return (null, null);
        }

        await using var scope = _scopeFactory.CreateAsyncScope();
        var bindingStore = scope.ServiceProvider.GetRequiredService<ITenantConnectorBindingStore>();
        var binding = await bindingStore
            .GetByExternalIdentityAsync(SlackInstallStore.ConnectorSlug, teamId, cancellationToken)
            .ConfigureAwait(false);
        if (binding is null)
        {
            return (null, null);
        }
        var config = binding.Config.Deserialize<TenantSlackConfig>(JsonOptions);
        return (binding, config);
    }

    private async Task<TenantBoundUser?> ResolveBoundUserAsync(
        string teamId,
        string slackUserId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(slackUserId))
        {
            return null;
        }

        await using var scope = _scopeFactory.CreateAsyncScope();
        var bindingStore = scope.ServiceProvider.GetRequiredService<ITenantConnectorBindingStore>();
        var bound = await bindingStore
            .GetBoundUsersAsync(SlackInstallStore.ConnectorSlug, cancellationToken)
            .ConfigureAwait(false);

        foreach (var b in bound)
        {
            if (string.Equals(b.ExternalUserId, slackUserId, StringComparison.Ordinal))
            {
                return b;
            }
        }
        return null;
    }

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

    private async Task RespondEphemerallyAsync(string channelId, string triggerId, string text, CancellationToken cancellationToken)
    {
        // For commands invoked in a non-DM channel we don't have a
        // bot token (we may not even be a member). The slash-command
        // response body is what Slack uses as the ephemeral reply;
        // the endpoint constructs that. This method exists so the
        // dispatcher's audit + non-DM refusal logic stays linear —
        // the actual response shape is built in
        // SlackCommandEndpoints.HandleAsync.
        _logger.LogInformation(
            "Slack slash: refusing non-DM invocation (channel={Channel} trigger={Trigger}).",
            channelId, triggerId);
        await Task.CompletedTask;
    }

    private static string GetForm(IReadOnlyDictionary<string, string> form, string key) =>
        form.TryGetValue(key, out var value) ? value : string.Empty;
}
