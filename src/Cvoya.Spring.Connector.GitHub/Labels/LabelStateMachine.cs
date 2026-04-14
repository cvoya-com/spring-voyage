// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connector.GitHub.Labels;

/// <summary>
/// Coordinator-protocol state machine over GitHub labels. Encodes the legal
/// transitions between the configured state labels so a single implementation
/// can be shared between:
///
/// <list type="bullet">
///   <item>the <c>github_label_transition</c> skill (request-time validation); and</item>
///   <item><see cref="Cvoya.Spring.Connector.GitHub.Webhooks.GitHubWebhookHandler"/>,
///   which attaches a <see cref="LabelStateTransition"/> to every
///   <c>issues.labeled</c> / <c>issues.unlabeled</c> dispatched message.</item>
/// </list>
///
/// The state set and transitions are fully configurable — the OSS default
/// matches the minimal v1 coordinator protocol but real deployments are
/// expected to override via configuration.
/// </summary>
public class LabelStateMachine
{
    private readonly HashSet<string> _states;
    private readonly Dictionary<string, HashSet<string>> _transitions;

    /// <summary>
    /// Initializes a new state machine from the supplied options. The options
    /// are snapshotted — subsequent mutations to the source object do not
    /// affect this instance.
    /// </summary>
    public LabelStateMachine(LabelStateMachineOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        _states = new HashSet<string>(options.States ?? [], StringComparer.OrdinalIgnoreCase);
        _transitions = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var kvp in options.Transitions ?? [])
        {
            _transitions[kvp.Key] = new HashSet<string>(kvp.Value ?? [], StringComparer.OrdinalIgnoreCase);
        }

        InitialState = string.IsNullOrWhiteSpace(options.InitialState) ? null : options.InitialState;
    }

    /// <summary>
    /// The configured initial state, or null when bootstrapping transitions
    /// from a blank slate is disallowed.
    /// </summary>
    public string? InitialState { get; }

    /// <summary>
    /// The full configured state set.
    /// </summary>
    public IReadOnlyCollection<string> States => _states;

    /// <summary>
    /// Returns true when the supplied label is part of the configured state set.
    /// Free-form labels (bug, enhancement, etc.) return false and must not
    /// drive state-machine logic.
    /// </summary>
    public bool IsStateLabel(string label) =>
        !string.IsNullOrWhiteSpace(label) && _states.Contains(label);

    /// <summary>
    /// Returns the legal destination states from the given source label. An
    /// unknown source returns an empty list.
    /// </summary>
    public IReadOnlyCollection<string> ValidTransitionsFrom(string from)
    {
        if (string.IsNullOrWhiteSpace(from) || !_transitions.TryGetValue(from, out var targets))
        {
            return [];
        }
        return targets;
    }

    /// <summary>
    /// Determines whether a transition between the two state labels is allowed.
    /// When <paramref name="from"/> is null (or empty), the transition is
    /// allowed only when <paramref name="to"/> equals <see cref="InitialState"/>.
    /// </summary>
    public bool IsLegalTransition(string? from, string? to)
    {
        if (string.IsNullOrWhiteSpace(to))
        {
            // Removal of a state label is treated as a no-op (not a transition).
            return false;
        }

        if (!_states.Contains(to))
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(from))
        {
            return InitialState is not null && string.Equals(InitialState, to, StringComparison.OrdinalIgnoreCase);
        }

        if (!_transitions.TryGetValue(from, out var targets))
        {
            return false;
        }

        return targets.Contains(to);
    }

    /// <summary>
    /// Derives the state transition represented by adding or removing a
    /// single label against the current label set. Returns null when the
    /// changed label is not in the configured state set — callers should
    /// treat that case as "no state change".
    /// </summary>
    /// <param name="currentLabels">The labels currently on the issue after the change.</param>
    /// <param name="changedLabel">The label that was added or removed.</param>
    /// <param name="action">The webhook action (<c>labeled</c> / <c>unlabeled</c>).</param>
    public LabelStateTransition? Derive(
        IReadOnlyCollection<string> currentLabels,
        string? changedLabel,
        string action)
    {
        ArgumentNullException.ThrowIfNull(action);

        if (string.IsNullOrWhiteSpace(changedLabel) || !_states.Contains(changedLabel))
        {
            return null;
        }

        // Other state labels present on the issue *excluding* the one that just changed.
        // GitHub delivers the post-change label set on both labeled and unlabeled
        // events, so for a labeled action "others" captures the previous dominant
        // state (if any).
        var otherStateLabels = (currentLabels ?? [])
            .Where(l => _states.Contains(l)
                && !string.Equals(l, changedLabel, StringComparison.OrdinalIgnoreCase))
            .ToArray();

        string? from;
        string? to;
        switch (action)
        {
            case "labeled":
                // Before: the pre-existing state label (if any). After: the newly added label.
                from = otherStateLabels.FirstOrDefault();
                to = changedLabel;
                break;
            case "unlabeled":
                // Before: the label that was removed. After: whatever remains (or null).
                from = changedLabel;
                to = otherStateLabels.FirstOrDefault();
                break;
            default:
                return null;
        }

        // Legal means "this is a recognized move in the state graph". Removal
        // with no remaining state (to == null) is always considered legal
        // because it represents a clean exit from the protocol.
        bool legal;
        if (string.IsNullOrWhiteSpace(to))
        {
            legal = true;
        }
        else
        {
            legal = IsLegalTransition(from, to);
        }

        return new LabelStateTransition(from, to, action, legal);
    }
}