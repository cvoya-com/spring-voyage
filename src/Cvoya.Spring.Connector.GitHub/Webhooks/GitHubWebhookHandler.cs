// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connector.GitHub.Webhooks;

using System.Text;
using System.Text.Json;

using Cvoya.Spring.Connector.GitHub.Auth;
using Cvoya.Spring.Connector.GitHub.Labels;
using Cvoya.Spring.Connectors;
using Cvoya.Spring.Core.Capabilities;
using Cvoya.Spring.Core.Messaging;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

/// <summary>
/// Processes incoming GitHub webhook payloads and translates them into
/// domain <see cref="Message"/> objects for the Spring Voyage platform.
///
/// <para>
/// Issue #2456 + ADR-0047 §10 — App-level delivery only, fan-out within
/// receiving tenant. Every inbound webhook arrives at the App-wide URL;
/// the handler keys on the payload's <c>(owner, repo)</c> within the
/// receiving tenant and returns one translated <see cref="Message"/> per
/// matching binding. Per-binding filters (issue #2407) decide which units
/// actually process the event. Deliveries that do not match any binding
/// are dropped silently (logged at Information so operators can correlate
/// noise).
/// </para>
/// </summary>
public class GitHubWebhookHandler : IGitHubWebhookHandler
{
    private static readonly JsonSerializerOptions ConfigJsonOptions = new(JsonSerializerDefaults.Web);

    private readonly LabelStateMachine _labelStateMachine;
    private readonly IUnitConnectorConfigStore? _configStore;
    private readonly IUnitConnectorBindingLookup? _bindingLookup;
    private readonly IActivityEventBus? _activityEventBus;
    private readonly IGitHubPullRequestFilesFetcher? _filesFetcher;
    private readonly IServiceScopeFactory? _scopeFactory;

    /// <summary>
    /// Initializes the handler. The <paramref name="labelStateMachine"/> is
    /// optional — when omitted (e.g. in legacy test setups) label-change events
    /// are still translated but carry no derived <c>state_transition</c>.
    /// </summary>
    /// <param name="loggerFactory">Logger factory.</param>
    /// <param name="labelStateMachine">Optional label state machine.</param>
    /// <param name="configStore">
    /// Optional per-unit connector binding store. When null, the inbound
    /// filter (issue #2407) is a no-op — every translated event passes.
    /// Real hosts (Host.Api) register the store; legacy / pure-unit-test
    /// fixtures don't, which preserves their existing behaviour.
    /// </param>
    /// <param name="bindingLookup">
    /// Connector-agnostic "list bindings of this type" seam used to fan
    /// out the inbound payload across every matching binding within the
    /// receiving tenant per ADR-0047 §10. When null, the handler has no
    /// way to address a translated message at a unit and returns an empty
    /// list from <see cref="TranslateEventAsync"/> — the connector treats
    /// that as "no binding matched", which surfaces to the webhook
    /// endpoint as 202 Accepted with no routing call. Real hosts always
    /// register the lookup; pure-unit-test fixtures may omit it when they
    /// only exercise the translate-shape path.
    /// </param>
    /// <param name="activityEventBus">
    /// Optional activity-event bus. When null, filter drops are silent.
    /// Real hosts always register the bus.
    /// </param>
    /// <param name="filesFetcher">
    /// Optional fetcher used to lazily hydrate a PR's changed-files list
    /// when (and only when) the unit binding configures
    /// <see cref="UnitGitHubConfig.IncludePaths"/>. When null, PR-shape
    /// events with no embedded <c>files</c> array fall through the path
    /// filter as if no files changed — which drops the event (consistent
    /// with the pure-evaluator semantics in <see cref="GitHubEventFilter"/>).
    /// Real hosts wire the OSS <see cref="OctokitGitHubPullRequestFilesFetcher"/>
    /// (or a cloud-provided substitute) so the filter is effective for every
    /// PR shape. Issue #2407.
    /// </param>
    /// <param name="scopeFactory">
    /// Optional scope factory used to resolve the scoped
    /// <see cref="IThreadRegistry"/> when stamping a participant-set thread
    /// id on each webhook-translated message (issue #2514). When null —
    /// e.g. legacy pure-unit-test fixtures — webhook-translated messages
    /// fall through with <c>ThreadId: null</c> and the persistent A2A
    /// dispatch path will refuse them; real hosts always wire this so the
    /// participant-set <c>(connector://github, unit://&lt;id&gt;)</c> resolves
    /// to a stable Guid-shaped thread per binding.
    /// </param>
    public GitHubWebhookHandler(
        ILoggerFactory loggerFactory,
        LabelStateMachine? labelStateMachine = null,
        IUnitConnectorConfigStore? configStore = null,
        IUnitConnectorBindingLookup? bindingLookup = null,
        IActivityEventBus? activityEventBus = null,
        IGitHubPullRequestFilesFetcher? filesFetcher = null,
        IServiceScopeFactory? scopeFactory = null)
    {
        _logger = loggerFactory.CreateLogger<GitHubWebhookHandler>();
        _labelStateMachine = labelStateMachine ?? new LabelStateMachine(LabelStateMachineOptions.Default());
        _configStore = configStore;
        _bindingLookup = bindingLookup;
        _activityEventBus = activityEventBus;
        _filesFetcher = filesFetcher;
        _scopeFactory = scopeFactory;
    }

    // Stable sentinel Guid for the GitHub connector's synthetic "from"
    // address. Pinned literal so it stays greppable in logs (the
    // trailing 12 hex chars spell "github" in ASCII).
    private static readonly Address ConnectorAddress =
        new(Address.ConnectorScheme, new Guid("00000000-0000-0000-0000-006769746875"));

    private readonly ILogger _logger;

    /// <summary>
    /// Translates a GitHub webhook event into one domain message per
    /// matching unit binding in the receiving tenant per ADR-0047 §10.
    /// Returns an empty list when:
    /// <list type="bullet">
    ///   <item><description>the event type is not handled,</description></item>
    ///   <item><description>no unit binding in the receiving tenant
    ///   matches the payload's <c>(owner, repo)</c> (silent drop — logged
    ///   at <c>Information</c>),</description></item>
    ///   <item><description>or no binding lookup is registered in DI.</description></item>
    /// </list>
    /// </summary>
    /// <param name="eventType">The GitHub event type from the X-GitHub-Event header.</param>
    /// <param name="payload">The parsed JSON payload.</param>
    /// <param name="cancellationToken">A token to cancel the binding lookup.</param>
    /// <returns>
    /// One unit-addressed domain <see cref="Message"/> per matching
    /// binding. Empty when the translation produced nothing or the
    /// delivery was dropped because no bound unit matched.
    /// </returns>
    public async Task<IReadOnlyList<Message>> TranslateEventAsync(
        string eventType, JsonElement payload, CancellationToken cancellationToken = default)
    {
        var translated = TranslatePayload(eventType, payload);
        if (translated is null)
        {
            return Array.Empty<Message>();
        }

        return await ResolveDestinationsAsync(translated, eventType, payload, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Translates an event into its domain-shaped payload without
    /// resolving a destination. Public so the connector's webhook entry
    /// point can call it directly when the binding lookup isn't required
    /// (e.g. unit tests that pin the payload shape).
    /// </summary>
    public Message? TranslatePayload(string eventType, JsonElement payload)
    {
        return eventType switch
        {
            "issues" => TranslateIssueEvent(payload),
            "pull_request" => TranslatePullRequestEvent(payload),
            "issue_comment" => TranslateIssueCommentEvent(payload),
            "pull_request_review" => TranslatePullRequestReviewEvent(payload),
            "pull_request_review_comment" => TranslatePullRequestReviewCommentEvent(payload),
            "pull_request_review_thread" => TranslatePullRequestReviewThreadEvent(payload),
            "installation" => TranslateInstallationEvent(payload),
            "installation_repositories" => TranslateInstallationRepositoriesEvent(payload),
            "projects_v2" => TranslateProjectsV2Event(payload),
            "projects_v2_item" => TranslateProjectsV2ItemEvent(payload),
            _ => null
        };
    }

    /// <summary>
    /// Resolves the destination units by matching the inbound webhook
    /// payload's <c>(owner, repo)</c> against every GitHub binding within
    /// the receiving tenant per ADR-0047 §10. Returns one copy of
    /// <paramref name="translated"/> per matching binding with <c>To</c>
    /// re-addressed to that binding's unit. Returns an empty list when no
    /// binding matched (silent drop, logged at <c>Information</c>).
    ///
    /// <para>
    /// The matcher's prior two-case shape (primary <c>(installation_id,
    /// owner, repo)</c> + installation-only fallback for org-level
    /// events) collapsed to a single <c>(owner, repo)</c> key once
    /// installation_id left the routing fabric. Cross-tenant collisions
    /// are rejected at binding-create time per ADR-0047 §10, so within
    /// any single tenant a payload for <c>(owner, repo)</c> is
    /// unambiguous about which bindings should receive it.
    /// </para>
    ///
    /// <para>
    /// Org-level events without repository coordinates (<c>installation</c>,
    /// <c>installation_repositories</c>, <c>projects_v2</c>,
    /// <c>projects_v2_item</c>) cannot match on <c>(owner, repo)</c> and
    /// therefore drop here. Phase D / E deliberately let them go: a
    /// supplementary subscription path for these will land alongside the
    /// portal lifecycle surface in a later phase; routing them through
    /// the <c>(owner, repo)</c> webhook key would mis-deliver under
    /// fan-out.
    /// </para>
    /// </summary>
    internal async Task<IReadOnlyList<Message>> ResolveDestinationsAsync(
        Message translated,
        string eventType,
        JsonElement webhookPayload,
        CancellationToken cancellationToken)
    {
        if (_bindingLookup is null)
        {
            // Tests that exercise the translate-shape path don't always
            // wire the lookup. Without a lookup we have no way to address
            // the event at any unit — drop it.
            _logger.LogInformation(
                "GitHub webhook: no IUnitConnectorBindingLookup registered; dropping {EventType} delivery.",
                SanitizeForLog(eventType));
            return Array.Empty<Message>();
        }

        var (owner, repo) = ExtractRoutingCoordinates(webhookPayload);

        // The lookup is tenant-scoped by the EF query filter on the
        // underlying binding repository, so iterating its result inside
        // the receiving tenant is the cross-tenant-safe shape: a binding
        // in another tenant that happens to point at the same (owner,
        // repo) is structurally invisible here.
        var bindings = await _bindingLookup
            .ListByConnectorTypeAsync(GitHubConnectorType.GitHubTypeId, cancellationToken)
            .ConfigureAwait(false);

        if (owner is null || repo is null || bindings.Count == 0)
        {
            _logger.LogInformation(
                "GitHub webhook: no binding matches delivery (owner={Owner}, repo={Repo}, event={EventType}); dropping silently.",
                SanitizeForLog(owner) ?? "<missing>",
                SanitizeForLog(repo) ?? "<missing>",
                SanitizeForLog(eventType));
            return Array.Empty<Message>();
        }

        // #2514: each translated fan-out message needs a stable Guid-shaped
        // ThreadId before it reaches MessageRouter — the persistent A2A
        // dispatch path mints a per-message SPRING_CALLBACK_TOKEN scoped to
        // the thread id (#1943) and throws otherwise. The participant set
        // is (connector://github, unit://<bindingUnitId>), so successive
        // deliveries from the same binding thread under the same
        // conversation. IThreadRegistry is scoped — open a single scope for
        // the loop so multiple matching bindings amortise the activation
        // cost, and rely on the registry's idempotent GetOrCreateAsync to
        // collapse duplicates on the participant set.
        var matches = new List<Message>();
        IServiceScope? threadScope = null;
        IThreadRegistry? threadRegistry = null;
        try
        {
            if (_scopeFactory is not null)
            {
                threadScope = _scopeFactory.CreateScope();
                threadRegistry = threadScope.ServiceProvider.GetRequiredService<IThreadRegistry>();
            }

            foreach (var entry in bindings)
            {
                UnitGitHubConfig? cfg;
                try
                {
                    cfg = entry.Binding.Config.Deserialize<UnitGitHubConfig>(ConfigJsonOptions);
                }
                catch (JsonException)
                {
                    continue;
                }
                if (cfg is null
                    || !UnitGitHubConfig.TryParseRepo(cfg.Repo, out var cfgOwner, out var cfgRepo))
                {
                    continue;
                }

                if (!string.Equals(cfgOwner, owner, StringComparison.OrdinalIgnoreCase)
                    || !string.Equals(cfgRepo, repo, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var destination = Address.For("unit", entry.UnitId);
                string? threadId = null;
                if (threadRegistry is not null)
                {
                    threadId = await threadRegistry.GetOrCreateAsync(
                        new[] { ConnectorAddress, destination }, cancellationToken)
                        .ConfigureAwait(false);
                }
                else
                {
                    // Defensive — production hosts always wire the scope
                    // factory. Pure-unit-test fixtures that exercise the
                    // resolve-shape without DI hit this path and produce
                    // a message with ThreadId: null; the persistent
                    // dispatch will refuse it. Logged as a warning so the
                    // gap is visible in any host that mis-registers.
                    _logger.LogWarning(
                        "GitHub webhook: no IServiceScopeFactory available to resolve IThreadRegistry; " +
                        "translated message for {Destination} will carry ThreadId: null (#2514).",
                        destination);
                }

                matches.Add(translated with
                {
                    Id = Guid.NewGuid(),
                    To = destination,
                    ThreadId = threadId,
                });
            }
        }
        finally
        {
            threadScope?.Dispose();
        }

        if (matches.Count == 0)
        {
            _logger.LogInformation(
                "GitHub webhook: no binding matches delivery (owner={Owner}, repo={Repo}, event={EventType}); dropping silently.",
                SanitizeForLog(owner) ?? "<missing>",
                SanitizeForLog(repo) ?? "<missing>",
                SanitizeForLog(eventType));
            return Array.Empty<Message>();
        }

        _logger.LogInformation(
            "GitHub webhook: routing {EventType} delivery (owner={Owner}, repo={Repo}) to {Count} binding(s).",
            SanitizeForLog(eventType),
            SanitizeForLog(owner),
            SanitizeForLog(repo),
            matches.Count);

        return matches;
    }

    private static (string? Owner, string? Repo) ExtractRoutingCoordinates(JsonElement payload)
    {
        string? owner = null;
        string? repo = null;
        if (payload.TryGetProperty("repository", out var r)
            && r.ValueKind == JsonValueKind.Object)
        {
            if (r.TryGetProperty("owner", out var o)
                && o.ValueKind == JsonValueKind.Object
                && o.TryGetProperty("login", out var login)
                && login.ValueKind == JsonValueKind.String)
            {
                owner = login.GetString();
            }
            if (r.TryGetProperty("name", out var n)
                && n.ValueKind == JsonValueKind.String)
            {
                repo = n.GetString();
            }
        }

        return (owner, repo);
    }

    /// <inheritdoc />
    public async Task<Message?> ApplyInboundFilterAsync(
        Message translated,
        string eventType,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(translated);

        // The translated message's To address is the unit the resolver
        // matched the inbound payload to (#2456). Per-binding filters
        // apply to that unit's binding. Without a config store wired
        // (legacy / unit-test fixtures) the filter is a no-op so the
        // existing behaviour is preserved exactly.
        if (_configStore is null
            || translated.To.Scheme != Address.UnitScheme)
        {
            return translated;
        }

        var unitHex = translated.To.Path;
        UnitConnectorBinding? binding;
        try
        {
            binding = await _configStore.GetAsync(unitHex, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // A failure looking up the binding (e.g. transient DB blip) is
            // not a reason to suppress the event — log and pass through.
            // The router will surface a downstream routing failure if the
            // unit really doesn't exist; we don't want a transient lookup
            // failure to mask a real subscription.
            _logger.LogWarning(ex,
                "GitHub inbound-filter: failed to load binding for unit {UnitHex}; passing event through unfiltered.",
                unitHex);
            return translated;
        }

        if (binding is null || binding.TypeId != GitHubConnectorType.GitHubTypeId)
        {
            // No GitHub binding on the target unit. Treat as "no filter
            // configured" — the message still flows; the router decides
            // whether the address resolves.
            return translated;
        }

        UnitGitHubConfig? config;
        try
        {
            config = binding.Config.Deserialize<UnitGitHubConfig>(ConfigJsonOptions);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex,
                "GitHub inbound-filter: stored binding config for unit {UnitHex} was not GitHub-shaped; passing event through unfiltered.",
                unitHex);
            return translated;
        }

        if (config is null)
        {
            return translated;
        }

        // Lazy enrichment for IncludePaths (issue #2407 — path filter coverage
        // expansion): the translated PR payload doesn't carry a `files` array
        // because the webhook body itself doesn't either. Rather than paying a
        // GET /pulls/{n}/files on every PR webhook, we fetch only when this
        // binding actually configures IncludePaths AND we have a PR-shape
        // translated event. Per-event review-comment payloads still carry
        // their own comment.path so the filter is effective there without
        // any fetch.
        var payloadForEvaluation = translated.Payload;
        if (HasIncludePaths(config) && _filesFetcher is not null)
        {
            var enriched = await TryEnrichWithChangedFilesAsync(
                config, translated.Payload, cancellationToken).ConfigureAwait(false);
            if (enriched is null)
            {
                // Fetcher signalled "fail open" — pass through unfiltered.
                _logger.LogWarning(
                    "GitHub inbound-filter: changed-files fetch failed for unit {UnitHex}; passing event through unfiltered.",
                    unitHex);
                return translated;
            }
            payloadForEvaluation = enriched.Value;
        }

        var result = GitHubEventFilter.Evaluate(config, payloadForEvaluation);
        if (result.Allowed)
        {
            return translated;
        }

        var safeEventType = SanitizeForLog(eventType);

        _logger.LogInformation(
            "GitHub inbound-filter: dropping event {EventType} for unit {UnitHex} — filter {FilterKind} matched {FilterValue}.",
            safeEventType, unitHex, result.Kind, result.Value);

        if (_activityEventBus is not null)
        {
            try
            {
                var details = JsonSerializer.SerializeToElement(new
                {
                    connector = "github",
                    event_type = eventType,
                    filter_kind = result.Kind,
                    filter_value = result.Value,
                });

                var activityEvent = new ActivityEvent(
                    Id: Guid.NewGuid(),
                    Timestamp: DateTimeOffset.UtcNow,
                    Source: translated.To,
                    EventType: ActivityEventType.ConnectorEventFiltered,
                    Severity: ActivitySeverity.Info,
                    Summary: $"GitHub {eventType} event dropped by {result.Kind} filter.",
                    Details: details);

                await _activityEventBus.PublishAsync(activityEvent, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // An activity-bus failure must not cause the webhook to
                // surface differently — the drop already happened; the
                // operator just doesn't get the audit signal. Log and move
                // on so the webhook endpoint still ACKs 202.
                _logger.LogWarning(ex,
                    "GitHub inbound-filter: failed to emit ConnectorEventFiltered activity event for unit {UnitHex}.",
                    unitHex);
            }
        }

        return null;
    }

    private static bool HasIncludePaths(UnitGitHubConfig config)
        => config.IncludePaths is { Count: > 0 };

    /// <summary>
    /// Strips control characters from attacker-controlled values before they
    /// reach the log stream, and caps length so a crafted header or payload
    /// field cannot forge fake log entries via CR/LF or flood logs. Mirrors
    /// the helper on <see cref="GitHubConnector"/>; duplicated locally so the
    /// webhook handler stays self-contained. Returns "unknown" for null/empty
    /// input so log messages remain readable.
    /// </summary>
    private static string SanitizeForLog(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return "unknown";
        }

        const int MaxLogValueLength = 128;
        var length = Math.Min(value.Length, MaxLogValueLength);
        var builder = new StringBuilder(length);
        for (var i = 0; i < length; i++)
        {
            var c = value[i];
            builder.Append(char.IsControl(c) ? '_' : c);
        }

        return builder.ToString();
    }

    /// <summary>
    /// Returns a translated payload with a hydrated <c>files</c> array when
    /// the event is PR-shape and the changed-files fetch succeeded. Returns
    /// the input payload unchanged when the event isn't PR-shape (path
    /// filter doesn't apply) or when a <c>files</c> array is already present
    /// (e.g. a review-comment event the translator wired). Returns <c>null</c>
    /// when the fetch was attempted and failed — caller treats that as
    /// "fail open" consistent with the binding-load failure pattern.
    /// </summary>
    private async Task<JsonElement?> TryEnrichWithChangedFilesAsync(
        UnitGitHubConfig config,
        JsonElement domainPayload,
        CancellationToken cancellationToken)
    {
        // PR-shape gate: only events whose translated payload carries a
        // "pull_request" object qualify. Pure issue events have no changed-
        // files surface — the filter ignores them by design (see
        // GitHubEventFilter remarks).
        if (!domainPayload.TryGetProperty("pull_request", out var pr)
            || pr.ValueKind != JsonValueKind.Object
            || !pr.TryGetProperty("number", out var numberEl)
            || numberEl.ValueKind != JsonValueKind.Number)
        {
            return domainPayload;
        }

        // If the translated payload already carries `files` (e.g. cloud-side
        // translator already enriched, or a future review-comment event with
        // a synthetic files entry), don't re-fetch.
        if (domainPayload.TryGetProperty("files", out var existingFiles)
            && existingFiles.ValueKind == JsonValueKind.Array
            && existingFiles.GetArrayLength() > 0)
        {
            return domainPayload;
        }

        if (!domainPayload.TryGetProperty("repository", out var repo)
            || repo.ValueKind != JsonValueKind.Object
            || !repo.TryGetProperty("owner", out var ownerEl)
            || ownerEl.ValueKind != JsonValueKind.String
            || !repo.TryGetProperty("name", out var nameEl)
            || nameEl.ValueKind != JsonValueKind.String)
        {
            // PR-shape but missing the repo coordinates we need for the
            // fetch — unusual but possible if the translator was extended
            // without back-filling repository. Skip enrichment; the
            // evaluator will read the empty files list and drop closed.
            return domainPayload;
        }

        var owner = ownerEl.GetString();
        var name = nameEl.GetString();
        var number = numberEl.GetInt32();

        if (string.IsNullOrEmpty(owner) || string.IsNullOrEmpty(name))
        {
            return domainPayload;
        }

        var fetched = await _filesFetcher!
            .FetchAsync(owner, name, number, config, cancellationToken)
            .ConfigureAwait(false);
        if (fetched is null)
        {
            // Fail open.
            return null;
        }

        // Re-serialise with a `files` property appended. The translated
        // payload is small enough that round-tripping it through a
        // dictionary is cheap relative to the network call we just made.
        var dict = new Dictionary<string, JsonElement>(domainPayload.EnumerateObject().Count() + 1);
        foreach (var prop in domainPayload.EnumerateObject())
        {
            dict[prop.Name] = prop.Value.Clone();
        }
        dict["files"] = JsonSerializer.SerializeToElement(fetched);
        return JsonSerializer.SerializeToElement(dict);
    }

    private Message? TranslateIssueEvent(JsonElement payload)
    {
        var action = payload.GetProperty("action").GetString();

        // Intent vocabulary aligns with v1's coordinator dispatch so downstream
        // units can switch on a single string rather than (event, action) pairs.
        return action switch
        {
            "opened" => CreateMessage(payload, "issue.opened", BuildIssuePayload(payload, "work_assignment", action)),
            "labeled" => CreateMessage(payload, "issue.labeled", BuildIssuePayload(payload, "label_change", action)),
            "unlabeled" => CreateMessage(payload, "issue.unlabeled", BuildIssuePayload(payload, "label_change", action)),
            "assigned" => CreateMessage(payload, "issue.assigned", BuildIssuePayload(payload, "assignment", action)),
            "unassigned" => CreateMessage(payload, "issue.unassigned", BuildIssuePayload(payload, "assignment", action)),
            "edited" => CreateMessage(payload, "issue.edited", BuildIssuePayload(payload, "edit", action)),
            "closed" => CreateMessage(payload, "issue.closed", BuildIssuePayload(payload, "lifecycle", action)),
            "reopened" => CreateMessage(payload, "issue.reopened", BuildIssuePayload(payload, "lifecycle", action)),
            _ => null
        };
    }

    private Message? TranslatePullRequestEvent(JsonElement payload)
    {
        var action = payload.GetProperty("action").GetString();

        return action switch
        {
            "opened" => CreateMessage(payload, "pull_request.opened", BuildPullRequestPayload(payload, "review_request", action)),
            "review_submitted" => CreateMessage(payload, "pull_request.review_submitted", BuildPullRequestPayload(payload, "review_result", action)),
            "synchronize" => CreateMessage(payload, "pull_request.synchronize", BuildPullRequestPayload(payload, "code_change", action)),
            "ready_for_review" => CreateMessage(payload, "pull_request.ready_for_review", BuildPullRequestPayload(payload, "review_request", action)),
            "converted_to_draft" => CreateMessage(payload, "pull_request.converted_to_draft", BuildPullRequestPayload(payload, "lifecycle", action)),
            "closed" => CreateMessage(payload, "pull_request.closed", BuildPullRequestPayload(payload, "lifecycle", action)),
            "edited" => CreateMessage(payload, "pull_request.edited", BuildPullRequestPayload(payload, "edit", action)),
            _ => null
        };
    }

    private Message? TranslateIssueCommentEvent(JsonElement payload)
    {
        var action = payload.GetProperty("action").GetString();

        return action switch
        {
            "created" => CreateMessage(payload, "issue_comment.created", BuildCommentPayload(payload, "feedback", action)),
            "edited" => CreateMessage(payload, "issue_comment.edited", BuildCommentPayload(payload, "feedback", action)),
            "deleted" => CreateMessage(payload, "issue_comment.deleted", BuildCommentPayload(payload, "feedback", action)),
            _ => null
        };
    }

    private Message CreateMessage(JsonElement webhookPayload, string eventName, JsonElement domainPayload)
    {
        // The To address is filled in by ResolveDestinationsAsync per
        // ADR-0047 §10 once the inbound payload's (owner, repo) has been
        // matched against the receiving tenant's bindings (one Message
        // per matching binding). CreateMessage stamps a placeholder
        // ("system://unresolved") so the message is well-formed for the
        // translate-shape unit tests that don't exercise the address
        // resolution path; production callers ALWAYS run the resolver
        // and overwrite To before the message is routed.
        string repoFullName = "unknown";
        if (webhookPayload.TryGetProperty("repository", out var repo)
            && repo.ValueKind == JsonValueKind.Object
            && repo.TryGetProperty("full_name", out var fn)
            && fn.ValueKind == JsonValueKind.String)
        {
            repoFullName = fn.GetString() ?? "unknown";
        }

        _logger.LogDebug(
            "Translating GitHub event {EventName} from {Repository}",
            eventName,
            repoFullName);

        return new Message(
            Id: Guid.NewGuid(),
            From: ConnectorAddress,
            To: UnresolvedDestination,
            Type: MessageType.Domain,
            ThreadId: null,
            Payload: domainPayload,
            Timestamp: DateTimeOffset.UtcNow);
    }

    /// <summary>
    /// Placeholder destination set on a freshly translated message before
    /// <see cref="ResolveDestinationAsync"/> matches the payload to a
    /// unit binding. The hex tail spells "unresolved" so the value is
    /// greppable in logs if it ever surfaces (it shouldn't — every
    /// translated message goes through the resolver).
    /// </summary>
    internal static readonly Address UnresolvedDestination =
        new("system", new Guid("00000000-0000-0000-0000-756e7265736f"));

    private JsonElement BuildIssuePayload(JsonElement payload, string intent, string? action)
    {
        var issue = payload.GetProperty("issue");
        var repo = payload.GetProperty("repository");

        // Action-specific delta fields — populated only when the webhook carries them,
        // mirroring v1's coordinator payload shape so downstream consumers can read
        // a consistent structure regardless of which action fired.
        string? changedLabel = null;
        if (payload.TryGetProperty("label", out var label) && label.ValueKind == JsonValueKind.Object)
        {
            changedLabel = label.GetProperty("name").GetString();
        }

        string? changedAssignee = null;
        if (payload.TryGetProperty("assignee", out var actionAssignee) && actionAssignee.ValueKind == JsonValueKind.Object)
        {
            changedAssignee = actionAssignee.GetProperty("login").GetString();
        }

        var labels = ExtractLabels(issue);

        // Derive a state_transition for labeled / unlabeled actions so downstream
        // agents can react without re-implementing the label state machine.
        LabelStateTransition? stateTransition = null;
        if (action is "labeled" or "unlabeled")
        {
            stateTransition = _labelStateMachine.Derive(labels, changedLabel, action);
        }

        var data = new
        {
            source = "github",
            intent,
            action,
            repository = new
            {
                owner = repo.GetProperty("owner").GetProperty("login").GetString(),
                name = repo.GetProperty("name").GetString(),
                full_name = repo.GetProperty("full_name").GetString()
            },
            issue = new
            {
                number = issue.GetProperty("number").GetInt32(),
                title = issue.GetProperty("title").GetString(),
                body = issue.TryGetProperty("body", out var body) ? body.GetString() : null,
                state = issue.TryGetProperty("state", out var state) ? state.GetString() : null,
                labels,
                assignee = issue.TryGetProperty("assignee", out var assignee) && assignee.ValueKind != JsonValueKind.Null
                    ? assignee.GetProperty("login").GetString()
                    : null,
                assignees = ExtractAssignees(issue),
                author = issue.TryGetProperty("user", out var user) && user.ValueKind == JsonValueKind.Object
                    ? user.GetProperty("login").GetString()
                    : null,
            },
            changed_label = changedLabel,
            changed_assignee = changedAssignee,
            state_transition = stateTransition,
        };

        return JsonSerializer.SerializeToElement(data);
    }

    private static JsonElement BuildPullRequestPayload(JsonElement payload, string intent, string? action)
    {
        var pr = payload.GetProperty("pull_request");
        var repo = payload.GetProperty("repository");

        var merged = pr.TryGetProperty("merged", out var m) && m.ValueKind == JsonValueKind.True;
        var draft = pr.TryGetProperty("draft", out var d) && d.ValueKind == JsonValueKind.True;

        var data = new
        {
            source = "github",
            intent,
            action,
            repository = new
            {
                owner = repo.GetProperty("owner").GetProperty("login").GetString(),
                name = repo.GetProperty("name").GetString(),
                full_name = repo.GetProperty("full_name").GetString()
            },
            pull_request = new
            {
                number = pr.GetProperty("number").GetInt32(),
                title = pr.GetProperty("title").GetString(),
                body = pr.TryGetProperty("body", out var body) ? body.GetString() : null,
                state = pr.TryGetProperty("state", out var state) ? state.GetString() : null,
                head = pr.GetProperty("head").GetProperty("ref").GetString(),
                @base = pr.GetProperty("base").GetProperty("ref").GetString(),
                draft,
                merged,
                author = pr.TryGetProperty("user", out var user) && user.ValueKind == JsonValueKind.Object
                    ? user.GetProperty("login").GetString()
                    : null,
            }
        };

        return JsonSerializer.SerializeToElement(data);
    }

    private static JsonElement BuildCommentPayload(JsonElement payload, string intent, string? action)
    {
        var comment = payload.GetProperty("comment");
        var issue = payload.GetProperty("issue");
        var repo = payload.GetProperty("repository");

        var data = new
        {
            source = "github",
            intent,
            action,
            repository = new
            {
                owner = repo.GetProperty("owner").GetProperty("login").GetString(),
                name = repo.GetProperty("name").GetString(),
                full_name = repo.GetProperty("full_name").GetString()
            },
            issue = new
            {
                number = issue.GetProperty("number").GetInt32(),
                title = issue.GetProperty("title").GetString()
            },
            comment = new
            {
                id = comment.GetProperty("id").GetInt64(),
                body = comment.GetProperty("body").GetString(),
                author = comment.GetProperty("user").GetProperty("login").GetString()
            }
        };

        return JsonSerializer.SerializeToElement(data);
    }

    private Message? TranslatePullRequestReviewEvent(JsonElement payload)
    {
        var action = payload.GetProperty("action").GetString();
        return action switch
        {
            "submitted" => CreateMessage(payload, "pull_request_review.submitted", BuildPullRequestReviewPayload(payload, "review_result", action)),
            "edited" => CreateMessage(payload, "pull_request_review.edited", BuildPullRequestReviewPayload(payload, "review_result", action)),
            "dismissed" => CreateMessage(payload, "pull_request_review.dismissed", BuildPullRequestReviewPayload(payload, "review_result", action)),
            _ => null,
        };
    }

    private Message? TranslatePullRequestReviewCommentEvent(JsonElement payload)
    {
        var action = payload.GetProperty("action").GetString();
        return action switch
        {
            "created" => CreateMessage(payload, "pull_request_review_comment.created", BuildPullRequestReviewCommentPayload(payload, "feedback", action)),
            "edited" => CreateMessage(payload, "pull_request_review_comment.edited", BuildPullRequestReviewCommentPayload(payload, "feedback", action)),
            "deleted" => CreateMessage(payload, "pull_request_review_comment.deleted", BuildPullRequestReviewCommentPayload(payload, "feedback", action)),
            _ => null,
        };
    }

    private Message? TranslatePullRequestReviewThreadEvent(JsonElement payload)
    {
        var action = payload.GetProperty("action").GetString();
        return action switch
        {
            "resolved" => CreateMessage(payload, "pull_request_review_thread.resolved", BuildPullRequestReviewThreadPayload(payload, "review_thread", action)),
            "unresolved" => CreateMessage(payload, "pull_request_review_thread.unresolved", BuildPullRequestReviewThreadPayload(payload, "review_thread", action)),
            _ => null,
        };
    }

    private Message? TranslateInstallationEvent(JsonElement payload)
    {
        var action = payload.GetProperty("action").GetString();
        return action switch
        {
            "created" => CreateMessage(payload, "installation.created", BuildInstallationPayload(payload, "installation_lifecycle", action)),
            "deleted" => CreateMessage(payload, "installation.deleted", BuildInstallationPayload(payload, "installation_lifecycle", action)),
            "suspend" => CreateMessage(payload, "installation.suspend", BuildInstallationPayload(payload, "installation_lifecycle", action)),
            "unsuspend" => CreateMessage(payload, "installation.unsuspend", BuildInstallationPayload(payload, "installation_lifecycle", action)),
            _ => null,
        };
    }

    private Message? TranslateInstallationRepositoriesEvent(JsonElement payload)
    {
        var action = payload.GetProperty("action").GetString();
        return action switch
        {
            "added" => CreateMessage(payload, "installation_repositories.added", BuildInstallationRepositoriesPayload(payload, "installation_repositories", action)),
            "removed" => CreateMessage(payload, "installation_repositories.removed", BuildInstallationRepositoriesPayload(payload, "installation_repositories", action)),
            _ => null,
        };
    }

    private static JsonElement BuildPullRequestReviewPayload(JsonElement payload, string intent, string? action)
    {
        var review = payload.GetProperty("review");
        var pr = payload.GetProperty("pull_request");
        var repo = payload.GetProperty("repository");

        var data = new
        {
            source = "github",
            intent,
            action,
            repository = new
            {
                owner = repo.GetProperty("owner").GetProperty("login").GetString(),
                name = repo.GetProperty("name").GetString(),
                full_name = repo.GetProperty("full_name").GetString(),
            },
            pull_request = new
            {
                number = pr.GetProperty("number").GetInt32(),
                title = pr.TryGetProperty("title", out var t) ? t.GetString() : null,
                author = pr.TryGetProperty("user", out var u) && u.ValueKind == JsonValueKind.Object
                    ? u.GetProperty("login").GetString()
                    : null,
            },
            review = new
            {
                id = review.TryGetProperty("id", out var rid) ? rid.GetInt64() : 0L,
                state = review.TryGetProperty("state", out var rs) ? rs.GetString() : null,
                body = review.TryGetProperty("body", out var rb) ? rb.GetString() : null,
                reviewer = review.TryGetProperty("user", out var ru) && ru.ValueKind == JsonValueKind.Object
                    ? ru.GetProperty("login").GetString()
                    : null,
                submitted_at = review.TryGetProperty("submitted_at", out var sa) && sa.ValueKind == JsonValueKind.String
                    ? sa.GetString()
                    : null,
            },
        };

        return JsonSerializer.SerializeToElement(data);
    }

    private static JsonElement BuildPullRequestReviewCommentPayload(JsonElement payload, string intent, string? action)
    {
        var comment = payload.GetProperty("comment");
        var pr = payload.GetProperty("pull_request");
        var repo = payload.GetProperty("repository");

        var data = new
        {
            source = "github",
            intent,
            action,
            repository = new
            {
                owner = repo.GetProperty("owner").GetProperty("login").GetString(),
                name = repo.GetProperty("name").GetString(),
                full_name = repo.GetProperty("full_name").GetString(),
            },
            pull_request = new
            {
                number = pr.GetProperty("number").GetInt32(),
                title = pr.TryGetProperty("title", out var t) ? t.GetString() : null,
            },
            comment = new
            {
                id = comment.GetProperty("id").GetInt64(),
                body = comment.TryGetProperty("body", out var cb) ? cb.GetString() : null,
                path = comment.TryGetProperty("path", out var cp) ? cp.GetString() : null,
                position = comment.TryGetProperty("position", out var pos) && pos.ValueKind == JsonValueKind.Number
                    ? pos.GetInt32()
                    : (int?)null,
                diff_hunk = comment.TryGetProperty("diff_hunk", out var dh) ? dh.GetString() : null,
                commit_id = comment.TryGetProperty("commit_id", out var ci) ? ci.GetString() : null,
                in_reply_to_id = comment.TryGetProperty("in_reply_to_id", out var rt) && rt.ValueKind == JsonValueKind.Number
                    ? rt.GetInt64()
                    : (long?)null,
                author = comment.TryGetProperty("user", out var cu) && cu.ValueKind == JsonValueKind.Object
                    ? cu.GetProperty("login").GetString()
                    : null,
            },
        };

        return JsonSerializer.SerializeToElement(data);
    }

    private static JsonElement BuildPullRequestReviewThreadPayload(JsonElement payload, string intent, string? action)
    {
        var thread = payload.GetProperty("thread");
        var pr = payload.GetProperty("pull_request");
        var repo = payload.GetProperty("repository");

        long? threadId = null;
        if (thread.TryGetProperty("node_id", out _) && thread.TryGetProperty("id", out var tid) && tid.ValueKind == JsonValueKind.Number)
        {
            threadId = tid.GetInt64();
        }

        string? nodeId = thread.TryGetProperty("node_id", out var nid) ? nid.GetString() : null;

        var data = new
        {
            source = "github",
            intent,
            action,
            repository = new
            {
                owner = repo.GetProperty("owner").GetProperty("login").GetString(),
                name = repo.GetProperty("name").GetString(),
                full_name = repo.GetProperty("full_name").GetString(),
            },
            pull_request = new
            {
                number = pr.GetProperty("number").GetInt32(),
                title = pr.TryGetProperty("title", out var t) ? t.GetString() : null,
            },
            thread = new
            {
                id = threadId,
                node_id = nodeId,
                resolved = string.Equals(action, "resolved", StringComparison.Ordinal),
            },
        };

        return JsonSerializer.SerializeToElement(data);
    }

    private static JsonElement BuildInstallationPayload(JsonElement payload, string intent, string? action)
    {
        var installation = payload.GetProperty("installation");

        string? reason = null;
        if (payload.TryGetProperty("sender", out var sender)
            && sender.ValueKind == JsonValueKind.Object
            && payload.TryGetProperty("suspended_by", out var sb)
            && sb.ValueKind == JsonValueKind.Object)
        {
            reason = "suspended_by_" + sb.GetProperty("login").GetString();
        }
        // GitHub includes "suspended_at" / "suspended_by" on installation payloads
        // when the installation is suspended; surface both verbatim for consumers
        // who need to persist the reason.
        string? suspendedAt = installation.TryGetProperty("suspended_at", out var sa) && sa.ValueKind == JsonValueKind.String
            ? sa.GetString()
            : null;
        string? suspendedBy = installation.TryGetProperty("suspended_by", out var ssb) && ssb.ValueKind == JsonValueKind.Object
            ? ssb.GetProperty("login").GetString()
            : null;

        var data = new
        {
            source = "github",
            intent,
            action,
            installation = new
            {
                id = installation.GetProperty("id").GetInt64(),
                account = installation.TryGetProperty("account", out var acct) && acct.ValueKind == JsonValueKind.Object
                    ? acct.GetProperty("login").GetString()
                    : null,
                account_type = installation.TryGetProperty("account", out var acct2) && acct2.ValueKind == JsonValueKind.Object
                    && acct2.TryGetProperty("type", out var at) ? at.GetString() : null,
                repository_selection = installation.TryGetProperty("repository_selection", out var rsel)
                    ? rsel.GetString()
                    : null,
                suspended_at = suspendedAt,
                suspended_by = suspendedBy,
            },
            repositories = ExtractInstallationRepositories(payload, "repositories"),
            suspension_reason = reason,
        };

        return JsonSerializer.SerializeToElement(data);
    }

    private static JsonElement BuildInstallationRepositoriesPayload(JsonElement payload, string intent, string? action)
    {
        var installation = payload.GetProperty("installation");

        var data = new
        {
            source = "github",
            intent,
            action,
            installation = new
            {
                id = installation.GetProperty("id").GetInt64(),
                account = installation.TryGetProperty("account", out var acct) && acct.ValueKind == JsonValueKind.Object
                    ? acct.GetProperty("login").GetString()
                    : null,
                repository_selection = installation.TryGetProperty("repository_selection", out var rsel)
                    ? rsel.GetString()
                    : null,
            },
            added_repositories = ExtractInstallationRepositories(payload, "repositories_added"),
            removed_repositories = ExtractInstallationRepositories(payload, "repositories_removed"),
        };

        return JsonSerializer.SerializeToElement(data);
    }

    private Message? TranslateProjectsV2Event(JsonElement payload)
    {
        var action = payload.GetProperty("action").GetString();
        // Projects v2 events fire at the org level (organization:<login> hook scope).
        // We translate the common lifecycle actions; unknown actions fall through to
        // null so the endpoint still acks without manufacturing a synthetic message.
        return action switch
        {
            "created" => CreateMessage(payload, "projects_v2.created", BuildProjectsV2Payload(payload, "project_lifecycle", action)),
            "edited" => CreateMessage(payload, "projects_v2.edited", BuildProjectsV2Payload(payload, "project_lifecycle", action)),
            "closed" => CreateMessage(payload, "projects_v2.closed", BuildProjectsV2Payload(payload, "project_lifecycle", action)),
            "reopened" => CreateMessage(payload, "projects_v2.reopened", BuildProjectsV2Payload(payload, "project_lifecycle", action)),
            "deleted" => CreateMessage(payload, "projects_v2.deleted", BuildProjectsV2Payload(payload, "project_lifecycle", action)),
            _ => null,
        };
    }

    private Message? TranslateProjectsV2ItemEvent(JsonElement payload)
    {
        var action = payload.GetProperty("action").GetString();
        return action switch
        {
            "created" => CreateMessage(payload, "projects_v2_item.created", BuildProjectsV2ItemPayload(payload, "project_item_lifecycle", action)),
            "edited" => CreateMessage(payload, "projects_v2_item.edited", BuildProjectsV2ItemPayload(payload, "project_item_change", action)),
            "archived" => CreateMessage(payload, "projects_v2_item.archived", BuildProjectsV2ItemPayload(payload, "project_item_lifecycle", action)),
            "restored" => CreateMessage(payload, "projects_v2_item.restored", BuildProjectsV2ItemPayload(payload, "project_item_lifecycle", action)),
            "deleted" => CreateMessage(payload, "projects_v2_item.deleted", BuildProjectsV2ItemPayload(payload, "project_item_lifecycle", action)),
            "converted" => CreateMessage(payload, "projects_v2_item.converted", BuildProjectsV2ItemPayload(payload, "project_item_lifecycle", action)),
            "reordered" => CreateMessage(payload, "projects_v2_item.reordered", BuildProjectsV2ItemPayload(payload, "project_item_change", action)),
            _ => null,
        };
    }

    private static JsonElement BuildProjectsV2Payload(JsonElement payload, string intent, string? action)
    {
        // projects_v2 webhook shape: top-level "projects_v2" plus "organization" (and
        // "installation" when org-installed). There is no "repository" field.
        var project = payload.TryGetProperty("projects_v2", out var p) && p.ValueKind == JsonValueKind.Object
            ? p
            : (JsonElement?)null;
        var orgLogin = payload.TryGetProperty("organization", out var org) && org.ValueKind == JsonValueKind.Object
            && org.TryGetProperty("login", out var ol) && ol.ValueKind == JsonValueKind.String
            ? ol.GetString()
            : null;

        var data = new
        {
            source = "github",
            intent,
            action,
            owner = orgLogin,
            project = project is { } pe ? new
            {
                id = pe.TryGetProperty("node_id", out var pid) && pid.ValueKind == JsonValueKind.String ? pid.GetString() : null,
                database_id = pe.TryGetProperty("id", out var did) && did.ValueKind == JsonValueKind.Number ? did.GetInt64() : 0L,
                number = pe.TryGetProperty("number", out var pn) && pn.ValueKind == JsonValueKind.Number ? pn.GetInt32() : 0,
                title = pe.TryGetProperty("title", out var pt) && pt.ValueKind == JsonValueKind.String ? pt.GetString() : null,
                closed = pe.TryGetProperty("closed", out var pc) && pc.ValueKind == JsonValueKind.True,
            } : null,
        };

        return JsonSerializer.SerializeToElement(data);
    }

    private static JsonElement BuildProjectsV2ItemPayload(JsonElement payload, string intent, string? action)
    {
        var item = payload.TryGetProperty("projects_v2_item", out var it) && it.ValueKind == JsonValueKind.Object
            ? it
            : (JsonElement?)null;
        var orgLogin = payload.TryGetProperty("organization", out var org) && org.ValueKind == JsonValueKind.Object
            && org.TryGetProperty("login", out var ol) && ol.ValueKind == JsonValueKind.String
            ? ol.GetString()
            : null;

        // field_value_changes fires only on "edited"; surface verbatim as JsonElement
        // so downstream consumers can inspect from/to without us re-encoding every shape.
        JsonElement? fieldChanges = null;
        if (payload.TryGetProperty("changes", out var changes) && changes.ValueKind == JsonValueKind.Object
            && changes.TryGetProperty("field_value", out var fv) && fv.ValueKind == JsonValueKind.Object)
        {
            fieldChanges = fv;
        }

        var data = new
        {
            source = "github",
            intent,
            action,
            owner = orgLogin,
            project_id = item is { } ie && ie.TryGetProperty("project_node_id", out var pid) && pid.ValueKind == JsonValueKind.String
                ? pid.GetString()
                : null,
            item = item is { } ie2 ? new
            {
                id = ie2.TryGetProperty("node_id", out var nid) && nid.ValueKind == JsonValueKind.String ? nid.GetString() : null,
                database_id = ie2.TryGetProperty("id", out var did) && did.ValueKind == JsonValueKind.Number ? did.GetInt64() : 0L,
                content_type = ie2.TryGetProperty("content_type", out var ct) && ct.ValueKind == JsonValueKind.String ? ct.GetString() : null,
                content_node_id = ie2.TryGetProperty("content_node_id", out var cnid) && cnid.ValueKind == JsonValueKind.String ? cnid.GetString() : null,
                archived = ie2.TryGetProperty("archived_at", out var ar) && ar.ValueKind == JsonValueKind.String,
            } : null,
            field_value_changes = fieldChanges,
        };

        return JsonSerializer.SerializeToElement(data);
    }

    private static object[] ExtractInstallationRepositories(JsonElement payload, string property)
    {
        if (!payload.TryGetProperty(property, out var arr) || arr.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return arr.EnumerateArray()
            .Where(r => r.ValueKind == JsonValueKind.Object)
            .Select(r => (object)new
            {
                id = r.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.Number ? id.GetInt64() : 0L,
                name = r.TryGetProperty("name", out var n) ? n.GetString() : null,
                full_name = r.TryGetProperty("full_name", out var fn) ? fn.GetString() : null,
                @private = r.TryGetProperty("private", out var p) && p.ValueKind == JsonValueKind.True,
            })
            .ToArray();
    }

    private static string[] ExtractLabels(JsonElement issue)
    {
        if (!issue.TryGetProperty("labels", out var labels) || labels.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return labels.EnumerateArray()
            .Select(l => l.GetProperty("name").GetString() ?? string.Empty)
            .Where(name => !string.IsNullOrEmpty(name))
            .ToArray();
    }

    private static string[] ExtractAssignees(JsonElement issue)
    {
        if (!issue.TryGetProperty("assignees", out var assignees) || assignees.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return assignees.EnumerateArray()
            .Where(a => a.ValueKind == JsonValueKind.Object)
            .Select(a => a.GetProperty("login").GetString() ?? string.Empty)
            .Where(name => !string.IsNullOrEmpty(name))
            .ToArray();
    }
}
