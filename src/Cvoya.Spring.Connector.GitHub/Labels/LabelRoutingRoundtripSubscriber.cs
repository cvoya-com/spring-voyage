// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connector.GitHub.Labels;

using System.Collections.Concurrent;
using System.Net;
using System.Reactive.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

using Cvoya.Spring.Connectors;
using Cvoya.Spring.Core.Capabilities;
using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Core.Orchestration;

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using Octokit;

/// <summary>
/// Hosted service that observes the platform activity bus for delegated
/// orchestration decisions and applies the configured GitHub label roundtrip
/// (<c>AddOnAssign</c> / <c>RemoveOnAssign</c>) on the originating issue.
/// </summary>
/// <remarks>
/// <para>
/// Core routing deliberately does not perform the label write because only
/// the connector holds the external GitHub credentials. Splitting the
/// responsibility along the event boundary keeps the core routing pipeline
/// unaware of the GitHub API surface and lets any other label-aware connector
/// subscribe to the same event shape without coupling back to the dispatcher.
/// </para>
/// <para>
/// Idempotency: GitHub's remove-label API returns 404 when the label is
/// already absent; the add-label API tolerates duplicates server-side. We
/// translate both into no-ops so a re-delivered assignment does not fault.
/// Permission / network errors are logged and swallowed — the subscription
/// must stay live so subsequent assignments still get processed.
/// </para>
/// </remarks>
public sealed class LabelRoutingRoundtripSubscriber : IHostedService, IDisposable
{
    private static readonly JsonSerializerOptions ConfigJson = new(JsonSerializerDefaults.Web);
    private static readonly JsonSerializerOptions DecisionJson = CreateDecisionJsonOptions();

    private readonly IActivityEventBus _bus;
    private readonly IGitHubConnector _connector;
    private readonly IUnitConnectorConfigStore _configStore;
    private readonly ILogger<LabelRoutingRoundtripSubscriber> _logger;
    private readonly CancellationTokenSource _shutdownCts = new();
    // Used as a concurrent set; the byte value is ignored. Tracks in-flight
    // handler tasks so StopAsync can drain them before the host tears down
    // the Dapr sidecar; otherwise the handlers' auth calls observe the
    // sidecar disappearing mid-flight and surface as class-cleanup gRPC
    // failures on unrelated tests that happen to share the WebApplicationFactory.
    private readonly ConcurrentDictionary<Task, byte> _inFlight = new();
    private IDisposable? _subscription;

    /// <summary>How long StopAsync waits for in-flight handlers to drain.</summary>
    internal static readonly TimeSpan DrainTimeout = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Creates a new <see cref="LabelRoutingRoundtripSubscriber"/>.
    /// </summary>
    public LabelRoutingRoundtripSubscriber(
        IActivityEventBus bus,
        IGitHubConnector connector,
        IUnitConnectorConfigStore configStore,
        ILogger<LabelRoutingRoundtripSubscriber> logger)
    {
        _bus = bus;
        _connector = connector;
        _configStore = configStore;
        _logger = logger;
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _subscription = _bus.ActivityStream
            .Where(IsRoutedDelegateDecision)
            .Subscribe(
                evt => TrackHandler(HandleEventAsync(evt, _shutdownCts.Token)),
                ex => _logger.LogError(
                    ex, "LabelRoutingRoundtripSubscriber stream faulted"));
        _logger.LogInformation(
            "LabelRoutingRoundtripSubscriber started — observing routed delegate decisions");
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _subscription?.Dispose();
        _subscription = null;

        // Cancel in-flight handlers so their auth / Octokit calls short-circuit
        // rather than racing the Dapr sidecar shutdown.
        try
        {
            _shutdownCts.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Already disposed via Dispose(); nothing to drain.
        }

        var pending = _inFlight.Keys.ToArray();
        if (pending.Length > 0)
        {
            try
            {
                await Task.WhenAll(pending)
                    .WaitAsync(DrainTimeout, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                _logger.LogWarning(
                    "Timed out draining {Count} in-flight label roundtrip(s) after {Timeout}",
                    pending.Length, DrainTimeout);
            }
            catch (OperationCanceledException)
            {
                // Host shutdown deadline reached; best-effort.
            }
            catch (Exception ex)
            {
                // Aggregate faults from handlers already logged inside HandleEventAsync.
                _logger.LogDebug(
                    ex, "Handler(s) faulted while draining; individual errors already logged");
            }
        }

        _logger.LogInformation("LabelRoutingRoundtripSubscriber stopped");
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _subscription?.Dispose();
        try
        {
            _shutdownCts.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // No-op.
        }
        _shutdownCts.Dispose();
    }

    /// <summary>
    /// Registers an in-flight handler task so <see cref="StopAsync"/> can drain
    /// it, and removes it from the tracking set when it completes.
    /// </summary>
    private void TrackHandler(Task task)
    {
        _inFlight.TryAdd(task, 0);
        task.ContinueWith(
            t => _inFlight.TryRemove(t, out _),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    /// <summary>
    /// Fire-and-forget task wrapper so one handler failure cannot fault the Rx
    /// subscription or block the synchronous <c>OnNext</c> callback. All
    /// exceptions are caught and logged inside the wrapper.
    /// </summary>
    private async Task HandleEventAsync(ActivityEvent evt, CancellationToken cancellationToken)
    {
        try
        {
            await ApplyRoundtripAsync(evt, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Shutdown; not a warning.
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Unhandled error applying label roundtrip for event {EventId}; continuing",
                evt.Id);
        }
    }

    /// <summary>
    /// Public-internal entry point for tests. Inspects the event's details,
    /// mints an authenticated client, and applies the label changes.
    /// </summary>
    internal async Task ApplyRoundtripAsync(ActivityEvent evt, CancellationToken cancellationToken)
    {
        if (!TryReadDecision(evt.Details, out var decision))
        {
            _logger.LogDebug(
                "Decision event {EventId} did not carry an orchestration decision; skipping", evt.Id);
            return;
        }

        if (decision.Kind != OrchestrationDecisionKind.Delegate
            || decision.Status != OrchestrationDecisionStatus.Routed)
        {
            _logger.LogDebug(
                "Decision event {EventId} is not a routed delegate decision; skipping", evt.Id);
            return;
        }

        if (!TryExtractIssueNumber(decision.Metadata, out var number))
        {
            _logger.LogDebug(
                "Decision event {EventId} did not carry an issue number; skipping", evt.Id);
            return;
        }

        var config = await LoadConfigAsync(decision.UnitAddress, cancellationToken)
            .ConfigureAwait(false);
        if (config is null)
        {
            _logger.LogDebug(
                "Decision event {EventId} did not resolve to a GitHub connector binding; skipping", evt.Id);
            return;
        }

        var owner = config.Owner;
        var repo = config.Repo;
        var addList = config.AddOnAssign ?? Array.Empty<string>();
        var removeList = config.RemoveOnAssign ?? Array.Empty<string>();
        if (addList.Count == 0 && removeList.Count == 0)
        {
            _logger.LogDebug(
                "Decision event {EventId} has no AddOnAssign / RemoveOnAssign labels; nothing to roundtrip",
                evt.Id);
            return;
        }

        IGitHubClient client;
        try
        {
            client = await _connector.CreateAuthenticatedClientAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Failed to mint authenticated GitHub client for label roundtrip on {Owner}/{Repo}#{Number}; skipping",
                owner, repo, number);
            return;
        }

        await ApplyWithClientAsync(client, owner, repo, number, addList, removeList, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Test-facing overload that skips the connector auth step so callers can
    /// inject a fake <see cref="IGitHubClient"/> directly. The side-effect set
    /// (removals first, then a single batched add) matches the production path.
    /// </summary>
    internal async Task ApplyWithClientAsync(
        IGitHubClient client,
        string owner,
        string repo,
        int number,
        IReadOnlyList<string> addList,
        IReadOnlyList<string> removeList,
        CancellationToken cancellationToken)
    {
        // Remove first so a label that appears in both lists resolves to
        // "added" (matches v1 behaviour). GitHub's DELETE /issues/:n/labels/:l
        // returns 404 when the label is absent on the issue; we treat that as
        // a no-op so re-delivery stays idempotent.
        foreach (var label in removeList)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            if (string.IsNullOrWhiteSpace(label))
            {
                continue;
            }
            try
            {
                await client.Issue.Labels
                    .RemoveFromIssue(owner, repo, number, label)
                    .ConfigureAwait(false);
            }
            catch (NotFoundException)
            {
                // Label not on issue OR issue was removed. Either way we
                // cannot roundtrip further; fall through and log.
                _logger.LogDebug(
                    "RemoveOnAssign label {Label} not present on {Owner}/{Repo}#{Number}; treating as no-op",
                    label, owner, repo, number);
            }
            catch (ForbiddenException ex)
            {
                _logger.LogWarning(
                    ex,
                    "Permission denied removing label {Label} from {Owner}/{Repo}#{Number}; aborting roundtrip",
                    label, owner, repo, number);
                return;
            }
            catch (ApiException ex) when (IsPermissionLike(ex))
            {
                _logger.LogWarning(
                    ex,
                    "GitHub rejected label removal for {Label} on {Owner}/{Repo}#{Number} (status {Status}); aborting roundtrip",
                    label, owner, repo, number, ex.StatusCode);
                return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "Transient error removing label {Label} from {Owner}/{Repo}#{Number}; continuing roundtrip",
                    label, owner, repo, number);
            }
        }

        if (addList.Count == 0)
        {
            return;
        }

        // Batch the adds — Octokit's AddToIssue takes a string[] and GitHub
        // tolerates duplicates server-side (the endpoint returns the full
        // label set afterwards, not just the new ones).
        var addArray = addList
            .Where(l => !string.IsNullOrWhiteSpace(l))
            .ToArray();
        if (addArray.Length == 0)
        {
            return;
        }
        try
        {
            await client.Issue.Labels
                .AddToIssue(owner, repo, number, addArray)
                .ConfigureAwait(false);
        }
        catch (NotFoundException ex)
        {
            _logger.LogWarning(
                ex,
                "Target issue {Owner}/{Repo}#{Number} not found when applying AddOnAssign labels; skipping",
                owner, repo, number);
        }
        catch (ForbiddenException ex)
        {
            _logger.LogWarning(
                ex,
                "Permission denied adding labels to {Owner}/{Repo}#{Number}; skipping",
                owner, repo, number);
        }
        catch (ApiException ex) when (IsPermissionLike(ex))
        {
            _logger.LogWarning(
                ex,
                "GitHub rejected label addition on {Owner}/{Repo}#{Number} (status {Status}); skipping",
                owner, repo, number, ex.StatusCode);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Transient error applying AddOnAssign labels to {Owner}/{Repo}#{Number}; skipping",
                owner, repo, number);
        }
    }

    /// <summary>
    /// Rx filter: only routed delegate orchestration decisions qualify.
    /// </summary>
    internal static bool IsRoutedDelegateDecision(ActivityEvent evt)
    {
        if (evt is null)
        {
            return false;
        }
        if (evt.EventType != ActivityEventType.DecisionMade)
        {
            return false;
        }
        if (evt.Details is null)
        {
            return false;
        }

        return TryReadDecision(evt.Details, out var decision)
            && decision.Kind == OrchestrationDecisionKind.Delegate
            && decision.Status == OrchestrationDecisionStatus.Routed;
    }

    private async Task<UnitGitHubConfig?> LoadConfigAsync(
        Address unitAddress,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(unitAddress.Scheme, Address.UnitScheme, StringComparison.Ordinal))
        {
            return null;
        }

        var binding = await _configStore.GetAsync(unitAddress.Path, cancellationToken)
            .ConfigureAwait(false);
        if (binding is null || binding.TypeId != GitHubConnectorType.GitHubTypeId)
        {
            return null;
        }

        try
        {
            return binding.Config.Deserialize<UnitGitHubConfig>(ConfigJson);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(
                ex,
                "Unit {UnitAddress} is bound to GitHub but the stored config could not be deserialized.",
                unitAddress);
            return null;
        }
    }

    /// <summary>
    /// Pulls the target issue number out of decision metadata. Returns
    /// <c>false</c> when anything critical is missing so the subscriber
    /// can log-and-skip rather than crash on malformed payloads.
    /// </summary>
    internal static bool TryExtractIssueNumber(JsonElement? metadata, out int number)
    {
        number = 0;

        if (metadata is null || metadata.Value.ValueKind != JsonValueKind.Object)
        {
            return false;
        }
        var root = metadata.Value;

        if (TryReadPositiveInt(root, "issueNumber", out number))
        {
            return true;
        }
        if (TryReadPositiveInt(root, "number", out number))
        {
            return true;
        }

        if (!root.TryGetProperty("issue", out var issueEl)
            || issueEl.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        return TryReadPositiveInt(issueEl, "number", out number);
    }

    private static bool TryReadDecision(
        JsonElement? details,
        out OrchestrationDecision decision)
    {
        decision = null!;
        if (details is null || details.Value.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        try
        {
            var parsed = details.Value.Deserialize<OrchestrationDecision>(DecisionJson);
            if (parsed is null)
            {
                return false;
            }

            decision = parsed;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool TryReadPositiveInt(JsonElement parent, string property, out int value)
    {
        value = 0;
        if (!parent.TryGetProperty(property, out var element)
            || element.ValueKind != JsonValueKind.Number
            || !element.TryGetInt32(out var parsed)
            || parsed <= 0)
        {
            return false;
        }

        value = parsed;
        return true;
    }

    private static JsonSerializerOptions CreateDecisionJsonOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter(allowIntegerValues: true));
        return options;
    }

    private static bool IsPermissionLike(ApiException ex) =>
        ex.StatusCode == HttpStatusCode.Forbidden
        || ex.StatusCode == HttpStatusCode.Unauthorized;
}
