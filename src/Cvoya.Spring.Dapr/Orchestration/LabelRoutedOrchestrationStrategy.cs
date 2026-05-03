// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Orchestration;

using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

using Cvoya.Spring.Core.Capabilities;
using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Core.Orchestration;
using Cvoya.Spring.Core.Policies;

using Microsoft.Extensions.Logging;

/// <summary>
/// Orchestration strategy that dispatches a message to a unit member based on
/// the labels declared on the inbound payload. Third concrete implementation
/// of <see cref="IOrchestrationStrategy"/> — see #389 for the acceptance
/// narrative and <see cref="LabelRoutingPolicy"/> for the matching rules.
/// </summary>
/// <remarks>
/// <para>
/// Matching is a <strong>case-insensitive set intersection</strong> over the
/// labels extracted from the message payload and the keys of
/// <see cref="LabelRoutingPolicy.TriggerLabels"/>. The first payload label
/// that hits the map — in the order the payload emits them — wins; the
/// matching member address is rebuilt from the unit's membership using the
/// mapped path. Ties are resolved by payload order so operators can influence
/// precedence by how the upstream connector enumerates labels (e.g. GitHub
/// webhooks list labels in apply order).
/// </para>
/// <para>
/// The strategy is deliberately conservative about un-configured input:
/// when the unit has no <see cref="LabelRoutingPolicy"/>, or the payload
/// carries no matching label, or the matched path is not a current member of
/// the unit, the strategy returns <c>null</c> and drops the message. This
/// matches the v1 "humans assign work by labels" behaviour: an untagged
/// issue is not picked up, full stop. Callers that want fallback-to-AI can
/// compose strategies at the host level by registering a decorator under a
/// different DI key.
/// </para>
/// <para>
/// Label extraction from the payload supports the two shapes that arrive
/// over the GitHub connector and over bare JSON:
/// <list type="bullet">
///   <item>
///     A top-level array of strings at <c>labels</c>: <c>{"labels": ["agent:backend"]}</c>.
///   </item>
///   <item>
///     A top-level array of objects at <c>labels</c> with a <c>name</c> field
///     (the GitHub webhook shape): <c>{"labels": [{"name": "agent:backend"}]}</c>.
///   </item>
/// </list>
/// Any other shape — missing <c>labels</c>, wrong type, nested differently —
/// is treated as "no labels" and the message is dropped. Expanding the
/// extraction surface to other connectors is additive and does not require
/// reshaping <see cref="LabelRoutingPolicy"/>.
/// </para>
/// <para>
/// Status-label roundtrip (<see cref="LabelRoutingPolicy.AddOnAssign"/> /
/// <see cref="LabelRoutingPolicy.RemoveOnAssign"/>) is not applied by the
/// strategy directly — that is the connector's responsibility because only
/// the connector holds the external credentials needed to mutate remote
/// state. Instead, after a successful forward the strategy publishes a
/// <see cref="ActivityEventType.DecisionMade"/> activity event carrying
/// <c>decision = "LabelRouted"</c>, the originating repository / issue
/// coordinates extracted from the message payload, and the policy's
/// <c>AddOnAssign</c> / <c>RemoveOnAssign</c> lists. The GitHub connector
/// subscribes to that event and performs the roundtrip (#492). Any other
/// label-aware connector can do the same without coupling to this
/// strategy — the contract is the event shape, not a direct dependency.
/// </para>
/// </remarks>
public class LabelRoutedOrchestrationStrategy : IOrchestrationStrategy
{
    /// <summary>
    /// Marker value placed on the emitted activity event's
    /// <c>decision</c> details field. Connectors (e.g. the GitHub
    /// connector's roundtrip subscriber) filter on this exact string.
    /// </summary>
    public const string LabelRoutedDecision = "LabelRouted";

    private readonly IUnitPolicyRepository _policyRepository;
    private readonly IActivityEventBus? _activityEventBus;
    private readonly ILogger _logger;

    /// <summary>
    /// Creates a new <see cref="LabelRoutedOrchestrationStrategy"/>.
    /// </summary>
    /// <param name="policyRepository">Per-unit policy lookup.</param>
    /// <param name="loggerFactory">Logger factory.</param>
    /// <param name="activityEventBus">
    /// Optional activity-event sink. When supplied, successful label-routed
    /// assignments publish a <see cref="ActivityEventType.DecisionMade"/>
    /// event so connectors (GitHub label roundtrip, #492) can observe
    /// the decision without a hard dependency on this assembly. When
    /// <c>null</c> — legacy test constructor path — the strategy behaves
    /// identically modulo the missing event, so existing unit tests keep
    /// passing.
    /// </param>
    public LabelRoutedOrchestrationStrategy(
        IUnitPolicyRepository policyRepository,
        ILoggerFactory loggerFactory,
        IActivityEventBus? activityEventBus = null)
    {
        _policyRepository = policyRepository;
        _activityEventBus = activityEventBus;
        _logger = loggerFactory.CreateLogger<LabelRoutedOrchestrationStrategy>();
    }

    /// <inheritdoc />
    public async Task<Message?> OrchestrateAsync(Message message, IUnitContext context, CancellationToken cancellationToken = default)
    {
        if (context.Members.Count == 0)
        {
            _logger.LogWarning(
                "Label-routed unit {UnitAddress} has no members to route to; dropping message {MessageId}",
                context.UnitAddress, message.Id);
            return null;
        }

        var policy = await _policyRepository.GetAsync(context.UnitAddress.Id, cancellationToken);
        var routing = policy.LabelRouting;

        if (routing is null || routing.TriggerLabels is null || routing.TriggerLabels.Count == 0)
        {
            _logger.LogInformation(
                "Label-routed unit {UnitAddress} has no LabelRoutingPolicy configured; dropping message {MessageId}",
                context.UnitAddress, message.Id);
            return null;
        }

        var payloadLabels = ExtractLabels(message.Payload);
        if (payloadLabels.Count == 0)
        {
            _logger.LogInformation(
                "Message {MessageId} to unit {UnitAddress} carries no labels; dropping",
                message.Id, context.UnitAddress);
            return null;
        }

        var (matchedLabel, matchedPath) = FindMatch(payloadLabels, routing.TriggerLabels);
        if (matchedLabel is null || matchedPath is null)
        {
            _logger.LogInformation(
                "Message {MessageId} to unit {UnitAddress} had labels [{Labels}] but none matched a trigger label; dropping",
                message.Id, context.UnitAddress, string.Join(",", payloadLabels));
            return null;
        }

        var target = ResolveMember(matchedPath, context.Members);
        if (target is null)
        {
            _logger.LogWarning(
                "Label {Label} on message {MessageId} maps to path {Path} which is not a current member of unit {UnitAddress}; dropping",
                matchedLabel, message.Id, matchedPath, context.UnitAddress);
            return null;
        }

        _logger.LogInformation(
            "Label-routed message {MessageId} via label {Label} to member {Target} (unit {UnitAddress})",
            message.Id, matchedLabel, target, context.UnitAddress);

        var forwarded = message with { To = target };
        var response = await context.SendAsync(forwarded, cancellationToken);

        // Publish the assignment decision so label-aware connectors can react
        // (e.g. GitHub applies AddOnAssign / RemoveOnAssign labels — #492).
        // Emission happens AFTER the forward succeeded so an un-routable
        // message does not trigger a roundtrip. Any failure to publish is
        // swallowed — the routing decision is the primary artefact; the
        // activity event is an optional side channel and must never take the
        // orchestration turn down.
        await PublishAssignmentEventAsync(
            message, context, matchedLabel, target, routing, cancellationToken);

        return response;
    }

    /// <summary>
    /// Publishes a <see cref="ActivityEventType.DecisionMade"/> event
    /// describing the label-routed assignment, including the originating
    /// source (e.g. <c>github</c>) and the roundtrip label lists the
    /// connector should apply. Best-effort: a null bus, missing payload
    /// source, or publish failure is logged and swallowed.
    /// </summary>
    private async Task PublishAssignmentEventAsync(
        Message message,
        IUnitContext context,
        string matchedLabel,
        Address target,
        LabelRoutingPolicy routing,
        CancellationToken cancellationToken)
    {
        var bus = _activityEventBus;
        if (bus is null)
        {
            return;
        }

        try
        {
            var details = BuildAssignmentDetails(message, context, matchedLabel, target, routing);
            var evt = new ActivityEvent(
                Id: Guid.NewGuid(),
                Timestamp: DateTimeOffset.UtcNow,
                Source: context.UnitAddress,
                EventType: ActivityEventType.DecisionMade,
                Severity: ActivitySeverity.Info,
                Summary: $"Label-routed message {message.Id} via {matchedLabel} to {target}",
                Details: details,
                CorrelationId: message.ThreadId);
            await bus.PublishAsync(evt, cancellationToken);
        }
        catch (Exception ex)
        {
            // A faulty subscriber must never fault the orchestration turn.
            _logger.LogWarning(
                ex,
                "Failed to publish label-routed assignment event for message {MessageId} on unit {UnitAddress}; continuing",
                message.Id, context.UnitAddress);
        }
    }

    /// <summary>
    /// Builds the <see cref="ActivityEvent.Details"/> payload for a
    /// label-routed assignment. Extracts <c>source</c>, <c>repository</c>,
    /// and <c>issue.number</c> from the inbound payload when present — these
    /// are the anchors the GitHub connector uses to target the API call.
    /// Shape is public-internal for test coverage.
    /// </summary>
    internal static JsonElement BuildAssignmentDetails(
        Message message,
        IUnitContext context,
        string matchedLabel,
        Address target,
        LabelRoutingPolicy routing)
    {
        string? source = TryGetString(message.Payload, "source");

        string? owner = null;
        string? repo = null;
        int? issueNumber = null;
        if (message.Payload.ValueKind == JsonValueKind.Object
            && message.Payload.TryGetProperty("repository", out var repoEl)
            && repoEl.ValueKind == JsonValueKind.Object)
        {
            owner = TryGetString(repoEl, "owner");
            repo = TryGetString(repoEl, "name");
        }

        if (message.Payload.ValueKind == JsonValueKind.Object
            && message.Payload.TryGetProperty("issue", out var issueEl)
            && issueEl.ValueKind == JsonValueKind.Object
            && issueEl.TryGetProperty("number", out var numEl)
            && numEl.ValueKind == JsonValueKind.Number
            && numEl.TryGetInt32(out var n))
        {
            issueNumber = n;
        }

        var payload = new
        {
            decision = LabelRoutedDecision,
            unitAddress = new { scheme = context.UnitAddress.Scheme, path = context.UnitAddress.Path },
            matchedLabel,
            target = new { scheme = target.Scheme, path = target.Path },
            source,
            repository = owner is not null || repo is not null
                ? new { owner, name = repo }
                : null,
            issue = issueNumber is not null
                ? new { number = issueNumber.Value }
                : null,
            addOnAssign = routing.AddOnAssign ?? (IReadOnlyList<string>)Array.Empty<string>(),
            removeOnAssign = routing.RemoveOnAssign ?? (IReadOnlyList<string>)Array.Empty<string>(),
            messageId = message.Id,
        };

        return JsonSerializer.SerializeToElement(payload);
    }

    private static string? TryGetString(JsonElement element, string propertyName)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return null;
        }
        if (!element.TryGetProperty(propertyName, out var value))
        {
            return null;
        }
        return value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    }

    /// <summary>
    /// Pulls label strings out of <paramref name="payload"/>. Accepts either
    /// <c>labels: ["name", ...]</c> or <c>labels: [{ "name": "..." }, ...]</c>.
    /// Returns an empty list for any other shape. Public-internal for unit
    /// test coverage of the parser.
    /// </summary>
    internal static IReadOnlyList<string> ExtractLabels(JsonElement payload)
    {
        if (payload.ValueKind != JsonValueKind.Object)
        {
            return Array.Empty<string>();
        }

        if (!payload.TryGetProperty("labels", out var labelsElement))
        {
            return Array.Empty<string>();
        }

        if (labelsElement.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<string>();
        }

        var result = new List<string>(labelsElement.GetArrayLength());
        foreach (var entry in labelsElement.EnumerateArray())
        {
            switch (entry.ValueKind)
            {
                case JsonValueKind.String:
                    var s = entry.GetString();
                    if (!string.IsNullOrWhiteSpace(s))
                    {
                        result.Add(s);
                    }
                    break;
                case JsonValueKind.Object:
                    if (entry.TryGetProperty("name", out var nameElement)
                        && nameElement.ValueKind == JsonValueKind.String)
                    {
                        var name = nameElement.GetString();
                        if (!string.IsNullOrWhiteSpace(name))
                        {
                            result.Add(name);
                        }
                    }
                    break;
                default:
                    // Silently ignore other shapes (numbers, bools, nulls) —
                    // they cannot be labels.
                    break;
            }
        }
        return result;
    }

    /// <summary>
    /// Finds the first payload label that hits the trigger map. Returns the
    /// matched label (preserving the payload's spelling) and the mapped
    /// target path (the policy's spelling). Matching is case-insensitive.
    /// </summary>
    internal static (string? Label, string? Path) FindMatch(
        IReadOnlyList<string> payloadLabels,
        IReadOnlyDictionary<string, string> triggerLabels)
    {
        // Build a case-insensitive lookup once per call. The dictionary we
        // receive from callers may have been constructed with the default
        // ordinal comparer (the JSON round-trip loses comparer identity), so
        // we can't rely on `TryGetValue` doing the case-insensitive lookup
        // for us.
        var lookup = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (k, v) in triggerLabels)
        {
            if (!string.IsNullOrWhiteSpace(k) && !string.IsNullOrWhiteSpace(v))
            {
                lookup[k] = v;
            }
        }

        foreach (var label in payloadLabels)
        {
            if (lookup.TryGetValue(label, out var path))
            {
                return (label, path);
            }
        }
        return (null, null);
    }

    /// <summary>
    /// Resolves <paramref name="path"/> against the unit's current members.
    /// The policy stores a bare path (e.g. <c>backend-engineer</c>); it is
    /// matched against member <c>Path</c> on both <c>agent://</c> and
    /// <c>unit://</c> schemes. Case-insensitive to stay consistent with
    /// label matching.
    /// </summary>
    internal static Address? ResolveMember(string path, IReadOnlyList<Address> members)
    {
        foreach (var member in members)
        {
            if (string.Equals(member.Path, path, StringComparison.OrdinalIgnoreCase))
            {
                return member;
            }
        }
        return null;
    }
}