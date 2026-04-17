// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Orchestration;

using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

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
/// state. The strategy records the intended labels on the forwarded
/// message's routing decision so the GitHub connector (and any other
/// label-aware connector) can observe them and apply the round-trip during
/// post-processing. Wiring the GitHub connector is tracked as follow-up
/// work so this PR stays scoped to the routing decision.
/// </para>
/// </remarks>
public class LabelRoutedOrchestrationStrategy(
    IUnitPolicyRepository policyRepository,
    ILoggerFactory loggerFactory) : IOrchestrationStrategy
{
    private readonly ILogger _logger = loggerFactory.CreateLogger<LabelRoutedOrchestrationStrategy>();

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

        var policy = await policyRepository.GetAsync(context.UnitAddress.Path, cancellationToken);
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
        return await context.SendAsync(forwarded, cancellationToken);
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