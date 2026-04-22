// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connector.GitHub.Labels;

using System.Collections.Concurrent;
using System.Net;
using System.Reactive.Linq;
using System.Text.Json;

using Cvoya.Spring.Core.Capabilities;

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using Octokit;

/// <summary>
/// Hosted service that observes the platform activity bus for label-routed
/// assignment events (<see cref="ActivityEventType.DecisionMade"/> with
/// <c>details.decision = "LabelRouted"</c> and <c>details.source = "github"</c>)
/// and applies the configured label roundtrip (<c>AddOnAssign</c> /
/// <c>RemoveOnAssign</c>) on the originating GitHub issue — closes #492.
/// </summary>
/// <remarks>
/// <para>
/// The strategy (<c>LabelRoutedOrchestrationStrategy</c>) deliberately does
/// not perform the label write because only the connector holds the external
/// GitHub credentials. Splitting the responsibility along the event boundary
/// keeps the core routing pipeline unaware of the GitHub API surface and lets
/// any other label-aware connector subscribe to the same event shape without
/// coupling back to the strategy.
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
    private readonly IActivityEventBus _bus;
    private readonly IGitHubConnector _connector;
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
        ILogger<LabelRoutingRoundtripSubscriber> logger)
    {
        _bus = bus;
        _connector = connector;
        _logger = logger;
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _subscription = _bus.ActivityStream
            .Where(IsLabelRoutedGitHubAssignment)
            .Subscribe(
                evt => TrackHandler(HandleEventAsync(evt, _shutdownCts.Token)),
                ex => _logger.LogError(
                    ex, "LabelRoutingRoundtripSubscriber stream faulted"));
        _logger.LogInformation(
            "LabelRoutingRoundtripSubscriber started — observing label-routed GitHub assignments");
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
        if (!TryExtractTarget(evt.Details, out var owner, out var repo, out var number,
            out var addList, out var removeList))
        {
            _logger.LogDebug(
                "Label-routed event {EventId} did not carry enough roundtrip context; skipping", evt.Id);
            return;
        }

        if (addList.Count == 0 && removeList.Count == 0)
        {
            _logger.LogDebug(
                "Label-routed event {EventId} has no AddOnAssign / RemoveOnAssign labels; nothing to roundtrip",
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
    /// Rx filter: only the <c>DecisionMade</c> events produced by the
    /// label-routing strategy for a GitHub-sourced message qualify.
    /// </summary>
    internal static bool IsLabelRoutedGitHubAssignment(ActivityEvent evt)
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
        var details = evt.Details.Value;
        if (details.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        if (!details.TryGetProperty("decision", out var decisionEl)
            || decisionEl.ValueKind != JsonValueKind.String
            || decisionEl.GetString() != "LabelRouted")
        {
            return false;
        }

        if (!details.TryGetProperty("source", out var sourceEl)
            || sourceEl.ValueKind != JsonValueKind.String
            || !string.Equals(sourceEl.GetString(), "github", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return true;
    }

    /// <summary>
    /// Pulls the roundtrip coordinates out of the event details. Returns
    /// <c>false</c> when anything critical is missing so the subscriber
    /// can log-and-skip rather than crash on malformed payloads.
    /// </summary>
    internal static bool TryExtractTarget(
        JsonElement? details,
        out string owner,
        out string repo,
        out int number,
        out IReadOnlyList<string> addList,
        out IReadOnlyList<string> removeList)
    {
        owner = string.Empty;
        repo = string.Empty;
        number = 0;
        addList = Array.Empty<string>();
        removeList = Array.Empty<string>();

        if (details is null || details.Value.ValueKind != JsonValueKind.Object)
        {
            return false;
        }
        var root = details.Value;

        if (!root.TryGetProperty("repository", out var repoEl)
            || repoEl.ValueKind != JsonValueKind.Object)
        {
            return false;
        }
        var ownerVal = TryGetString(repoEl, "owner");
        var repoVal = TryGetString(repoEl, "name");
        if (string.IsNullOrWhiteSpace(ownerVal) || string.IsNullOrWhiteSpace(repoVal))
        {
            return false;
        }

        if (!root.TryGetProperty("issue", out var issueEl)
            || issueEl.ValueKind != JsonValueKind.Object
            || !issueEl.TryGetProperty("number", out var numEl)
            || numEl.ValueKind != JsonValueKind.Number
            || !numEl.TryGetInt32(out var numVal)
            || numVal <= 0)
        {
            return false;
        }

        owner = ownerVal!;
        repo = repoVal!;
        number = numVal;
        addList = ReadStringArray(root, "addOnAssign");
        removeList = ReadStringArray(root, "removeOnAssign");
        return true;
    }

    private static string? TryGetString(JsonElement parent, string property)
    {
        if (!parent.TryGetProperty(property, out var el))
        {
            return null;
        }
        return el.ValueKind == JsonValueKind.String ? el.GetString() : null;
    }

    private static IReadOnlyList<string> ReadStringArray(JsonElement parent, string property)
    {
        if (!parent.TryGetProperty(property, out var arr) || arr.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<string>();
        }
        var result = new List<string>(arr.GetArrayLength());
        foreach (var entry in arr.EnumerateArray())
        {
            if (entry.ValueKind == JsonValueKind.String)
            {
                var s = entry.GetString();
                if (!string.IsNullOrWhiteSpace(s))
                {
                    result.Add(s);
                }
            }
        }
        return result;
    }

    private static bool IsPermissionLike(ApiException ex) =>
        ex.StatusCode == HttpStatusCode.Forbidden
        || ex.StatusCode == HttpStatusCode.Unauthorized;
}