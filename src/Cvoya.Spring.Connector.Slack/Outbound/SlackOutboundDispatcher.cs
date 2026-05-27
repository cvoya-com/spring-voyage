// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connector.Slack.Outbound;

using System.Text.Json;

using Cvoya.Spring.Connector.Slack.Auth.OAuth;
using Cvoya.Spring.Connector.Slack.Routing;
using Cvoya.Spring.Connector.Slack.Slug;
using Cvoya.Spring.Connector.Slack.WebApi;
using Cvoya.Spring.Connectors;
using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Core.Secrets;
using Cvoya.Spring.Core.Tenancy;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

/// <summary>
/// Default <see cref="ISlackOutboundDispatcher"/>. Composes the
/// routing function, the thread-map store, the Slack Web API client,
/// and the persona builder to deliver an SV-side outbound message
/// into the bound user's Slack DM as a threaded reply.
/// </summary>
/// <remarks>
/// <para>
/// Singleton. All scoped collaborators
/// (<see cref="ITenantConnectorBindingStore"/>,
/// <see cref="ITenantContext"/>, <see cref="ISecretResolver"/>) are
/// resolved per call through <see cref="IServiceScopeFactory"/> — the
/// same singleton-safety pattern <see cref="SlackInstallStore"/> uses.
/// </para>
/// </remarks>
public sealed class SlackOutboundDispatcher : ISlackOutboundDispatcher
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ISlackContainerRouter _router;
    private readonly ISlackThreadSlugBuilder _slugBuilder;
    private readonly ISlackPersonaBuilder _personaBuilder;
    private readonly ISlackWebApiClient _webApi;
    private readonly ISlackThreadMapStore _threadMap;
    private readonly ILogger<SlackOutboundDispatcher> _logger;

    /// <summary>Creates a new <see cref="SlackOutboundDispatcher"/>.</summary>
    public SlackOutboundDispatcher(
        IServiceScopeFactory scopeFactory,
        ISlackContainerRouter router,
        ISlackThreadSlugBuilder slugBuilder,
        ISlackPersonaBuilder personaBuilder,
        ISlackWebApiClient webApi,
        ISlackThreadMapStore threadMap,
        ILoggerFactory loggerFactory)
    {
        ArgumentNullException.ThrowIfNull(scopeFactory);
        _scopeFactory = scopeFactory;
        _router = router;
        _slugBuilder = slugBuilder;
        _personaBuilder = personaBuilder;
        _webApi = webApi;
        _threadMap = threadMap;
        _logger = loggerFactory.CreateLogger<SlackOutboundDispatcher>();
    }

    /// <inheritdoc />
    public async Task<SlackOutboundResult> DispatchAsync(
        Message message,
        IReadOnlyList<Address> participants,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(participants);

        await using var scope = _scopeFactory.CreateAsyncScope();
        var sp = scope.ServiceProvider;
        var bindingStore = sp.GetRequiredService<ITenantConnectorBindingStore>();

        // 1. Load the tenant's Slack binding. No binding = not installed.
        var binding = await bindingStore.GetAsync(SlackInstallStore.ConnectorSlug, cancellationToken).ConfigureAwait(false);
        if (binding is null)
        {
            return SlackOutboundResult.NotBound;
        }

        var config = binding.Config.Deserialize<TenantSlackConfig>(JsonOptions);
        if (config is null)
        {
            _logger.LogWarning("Slack outbound dispatch: binding config payload was not TenantSlackConfig-shaped; dropping message {MessageId}.", message.Id);
            return SlackOutboundResult.NotBound;
        }

        // 2. Get the bound-user list. ADR-0061 §7.1: iterate; length 1
        //    in OSS, length N in cloud.
        var boundUsers = await bindingStore
            .GetBoundUsersAsync(SlackInstallStore.ConnectorSlug, cancellationToken)
            .ConfigureAwait(false);

        // 3. Resolve the routing decision via the central function so
        //    no caller hardcodes "container == DM" (ADR-0061 §7.8).
        //    The participant set carries human:// addresses whose
        //    Address.Id is a Human row id; the router compares against
        //    TenantUserId. The caller is responsible for the resolution
        //    — when participants contain a Hat id, the resolver must
        //    have already replaced it with the bound TenantUser id, or
        //    the router will count no bound humans. For the OSS happy
        //    path the platform's message-construction site already
        //    rewrites Message.From through ITenantUserHumanResolver
        //    (ADR-0062 §3), so the calling code can pass that
        //    resolved Address verbatim.
        var resolvedParticipants = await ResolveHumanParticipantsToBoundUsersAsync(
            sp, participants, cancellationToken).ConfigureAwait(false);

        var route = _router.Route(resolvedParticipants, boundUsers);
        if (route is SlackContainerRoute.None)
        {
            return SlackOutboundResult.NoSlackSurface;
        }

        if (route is SlackContainerRoute.PrivateChannel)
        {
            // ADR-0061 §7.2: branch is reachable for forward-compat;
            // consuming it in v0.1 throws. The seam preserves the
            // shape; the hybrid-mode implementation will replace the
            // throw with the channel-create + post path.
            throw new NotSupportedException(
                "PrivateChannel routing reserved for hybrid mode — ADR-0061 §7.2");
        }

        var dm = (SlackContainerRoute.DirectMessage)route;
        var boundUser = boundUsers.First(b => string.Equals(b.ExternalUserId, dm.SlackUserId, StringComparison.Ordinal));

        var botToken = await ReadBotTokenAsync(sp, config, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrEmpty(botToken))
        {
            _logger.LogWarning("Slack outbound dispatch: bot token not resolvable for tenant; dropping message {MessageId}.", message.Id);
            return SlackOutboundResult.NotBound;
        }

        // 4. Open the DM and resolve the channel id. Idempotent.
        var open = await _webApi.OpenConversationAsync(botToken, dm.SlackUserId, cancellationToken).ConfigureAwait(false);
        if (!open.Ok || string.IsNullOrEmpty(open.ChannelId))
        {
            _logger.LogWarning("Slack outbound dispatch: conversations.open failed (error={Error}); dropping message {MessageId}.", open.Error, message.Id);
            return SlackOutboundResult.NotBound;
        }

        // 5. Look up the thread mapping. First post on this SV thread
        //    for this bound user → post a parent message with the
        //    slug, then store the thread_ts. Subsequent posts → use
        //    the stored thread_ts.
        var svThreadId = TryParseThreadId(message.ThreadId);
        if (svThreadId is null)
        {
            _logger.LogWarning("Slack outbound dispatch: message {MessageId} has no parseable thread id; dropping.", message.Id);
            return SlackOutboundResult.NotBound;
        }

        var existing = await _threadMap
            .LookupOutboundAsync(svThreadId.Value, boundUser.TenantUserId, config.TeamId, cancellationToken)
            .ConfigureAwait(false);

        string threadTs;
        if (existing is null)
        {
            // First-post path: build the slug, post the parent, persist.
            var slug = await _slugBuilder.BuildSlugAsync(
                participants,
                boundUser.TenantUserId,
                svThreadId.Value,
                cancellationToken).ConfigureAwait(false);

            var parent = await _webApi.PostMessageAsync(
                botToken,
                open.ChannelId,
                slug,
                threadTs: null,
                username: null,
                iconUrl: null,
                cancellationToken).ConfigureAwait(false);

            if (!parent.Ok || string.IsNullOrEmpty(parent.MessageTs))
            {
                _logger.LogWarning(
                    "Slack outbound dispatch: parent post failed for SV thread {SvThreadId} (error={Error}); dropping message {MessageId}.",
                    svThreadId.Value, parent.Error, message.Id);
                return SlackOutboundResult.NotBound;
            }

            await _threadMap.RecordAsync(
                svThreadId.Value,
                boundUser.TenantUserId,
                config.TeamId,
                parent.ChannelId,
                parent.MessageTs,
                cancellationToken).ConfigureAwait(false);

            threadTs = parent.MessageTs;
        }
        else
        {
            threadTs = existing.SlackThreadTs;
        }

        // 6. Post the actual message as a threaded reply with the
        //    persona of the SV-side sender. The bound user's own DM
        //    messages would surface as themselves; here we are
        //    posting on behalf of an agent / unit / non-bound human.
        var persona = await _personaBuilder.ResolveAsync(message.From, cancellationToken).ConfigureAwait(false);

        var text = ExtractMessageText(message.Payload);

        var reply = await _webApi.PostMessageAsync(
            botToken,
            open.ChannelId,
            text,
            threadTs: threadTs,
            username: persona.Username,
            iconUrl: persona.IconUrl,
            cancellationToken).ConfigureAwait(false);

        if (!reply.Ok)
        {
            _logger.LogWarning(
                "Slack outbound dispatch: reply post failed for SV thread {SvThreadId} thread_ts={ThreadTs} (error={Error}).",
                svThreadId.Value, threadTs, reply.Error);
            return SlackOutboundResult.NotBound;
        }

        _logger.LogInformation(
            "Slack outbound dispatch: posted SV message {MessageId} to thread_ts={ThreadTs} channel={Channel} as {Persona}.",
            message.Id, threadTs, open.ChannelId, persona.Username);

        return SlackOutboundResult.Delivered;
    }

    private async Task<IReadOnlyList<Address>> ResolveHumanParticipantsToBoundUsersAsync(
        IServiceProvider sp,
        IReadOnlyList<Address> participants,
        CancellationToken cancellationToken)
    {
        // ADR-0062 §3: every Human row carries a TenantUserId FK. The
        // routing function matches against the bound-user list's
        // TenantUserId values. Resolve every human:// participant via
        // the resolver so the router sees the right id space.
        var resolver = sp.GetService<ITenantUserHumanResolver>();
        if (resolver is null)
        {
            // No resolver wired — caller has either already resolved
            // or is on a test path that doesn't need humans in the
            // mix. Pass the addresses through unchanged.
            return participants;
        }

        var result = new List<Address>(participants.Count);
        foreach (var addr in participants)
        {
            if (addr is null)
            {
                continue;
            }

            if (!string.Equals(addr.Scheme, Address.HumanScheme, StringComparison.Ordinal))
            {
                result.Add(addr);
                continue;
            }

            // The resolver's contract is "given a TenantUser, return
            // their Hat". We have the opposite — a Hat (Human row id)
            // and need the TenantUser. Look it up directly through
            // the EF context, since the resolver doesn't surface the
            // reverse map. Defer the lookup to a separate scoped
            // service; for v0.1 we read the bound-user list mapping
            // and assume the human's TenantUser matches one of the
            // bound entries (OSS shape: every human → operator).
            //
            // Simpler v0.1 shape: pass the Human id through; the
            // platform's outbound construction site has already
            // rewritten Message.From through PickFromAsync — the
            // routing function is therefore comparing against the
            // already-resolved Hat id. For OSS the bound-user list's
            // TenantUserId is OssTenantUserIds.Operator; for the
            // happy path the participant address's id is the Hat id
            // (Human row id), NOT the TenantUser id. The router will
            // therefore find zero matches unless we resolve here.
            //
            // The cleanest resolve uses the same IHumanIdentityResolver
            // surface the inbox resolver uses — but that returns
            // display names, not the FK. Read the FK directly via the
            // sibling IHumanTenantUserLookup (defined as part of this
            // wave so the resolution stays explicit).
            var lookup = sp.GetService<IHumanTenantUserLookup>();
            if (lookup is null)
            {
                result.Add(addr);
                continue;
            }

            var tenantUserId = await lookup
                .GetTenantUserIdAsync(addr.Id, cancellationToken)
                .ConfigureAwait(false);

            if (tenantUserId is null)
            {
                result.Add(addr);
                continue;
            }

            // Substitute the participant address with one whose id is
            // the bound TenantUser id, preserving the scheme so the
            // router's existing match shape works unchanged.
            result.Add(new Address(Address.HumanScheme, tenantUserId.Value));
        }

        return result;
    }

    private static async Task<string?> ReadBotTokenAsync(
        IServiceProvider sp,
        TenantSlackConfig config,
        CancellationToken cancellationToken)
    {
        var resolver = sp.GetRequiredService<ISecretResolver>();
        var tenantContext = sp.GetRequiredService<ITenantContext>();

        var resolution = await resolver
            .ResolveWithPathAsync(
                new SecretRef(SecretScope.Tenant, tenantContext.CurrentTenantId, config.BotTokenSecretName),
                cancellationToken)
            .ConfigureAwait(false);

        return resolution.Value;
    }

    private static Guid? TryParseThreadId(string? threadId)
    {
        if (string.IsNullOrEmpty(threadId))
        {
            return null;
        }

        return Guid.TryParse(threadId, out var id) ? id : null;
    }

    private static string ExtractMessageText(JsonElement payload)
    {
        // The agent-facing envelope's payload shape carries a "text"
        // or "body" field for human-readable content per ADR-0060.
        // Fall back to the raw JSON form for non-text payloads (the
        // bound user is the only Slack-visible consumer; they will
        // see the raw shape and can ask the agent for clarification
        // — better than dropping the message silently).
        if (payload.ValueKind == JsonValueKind.Object)
        {
            if (payload.TryGetProperty("text", out var textProp)
                && textProp.ValueKind == JsonValueKind.String)
            {
                return textProp.GetString() ?? string.Empty;
            }
            if (payload.TryGetProperty("body", out var bodyProp)
                && bodyProp.ValueKind == JsonValueKind.String)
            {
                return bodyProp.GetString() ?? string.Empty;
            }
        }

        if (payload.ValueKind == JsonValueKind.String)
        {
            return payload.GetString() ?? string.Empty;
        }

        return payload.GetRawText();
    }
}

/// <summary>
/// Reverse lookup from a <c>Human</c> row id to the bound
/// <c>TenantUser</c>. Sits between the connector and the EF
/// context — implemented in <c>Cvoya.Spring.Dapr</c> so the connector
/// stays free of EF dependencies (CONVENTIONS §16).
/// </summary>
public interface IHumanTenantUserLookup
{
    /// <summary>
    /// Returns the <c>TenantUser.Id</c> bound to <paramref name="humanId"/>
    /// per ADR-0062 §1, or <c>null</c> when the human row does not
    /// exist in the current tenant.
    /// </summary>
    Task<Guid?> GetTenantUserIdAsync(Guid humanId, CancellationToken cancellationToken = default);
}
