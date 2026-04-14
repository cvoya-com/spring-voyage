// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connector.GitHub.Labels;

/// <summary>
/// Configuration for the GitHub label state machine. Customers can override the
/// <see cref="States"/>, <see cref="Transitions"/>, and <see cref="InitialState"/>
/// via configuration (<c>GitHub:Labels</c> section) so that per-unit coordinator
/// protocols can diverge from the OSS default. The OSS default captures the
/// minimal v1 coordinator protocol (<c>needs-triage</c> → <c>in-progress</c> →
/// <c>blocked</c> | <c>resolved</c>). Real-world setups are expected to replace
/// this with their own label vocabulary.
/// </summary>
public class LabelStateMachineOptions
{
    /// <summary>
    /// The full set of label names that participate in the state machine. Labels
    /// outside this set are treated as free-form metadata and do not trigger
    /// transitions when they are added or removed from an issue / PR.
    /// </summary>
    public List<string> States { get; set; } = [];

    /// <summary>
    /// Allowed transitions encoded as <c>from → [to, ...]</c>. The key is the
    /// source state (label name currently on the issue) and the value is the
    /// list of legal destination states the coordinator may move to.
    /// </summary>
    public Dictionary<string, List<string>> Transitions { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Optional initial state, used when a transition is requested on an issue
    /// that carries no state label yet. Leave empty to disallow bootstrapping
    /// transitions from a blank slate.
    /// </summary>
    public string? InitialState { get; set; }

    /// <summary>
    /// Returns the default OSS state machine, matching the minimal v1 coordinator
    /// protocol. Real deployments should override via configuration.
    /// </summary>
    public static LabelStateMachineOptions Default() => new()
    {
        States = ["needs-triage", "in-progress", "blocked", "resolved"],
        Transitions = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["needs-triage"] = ["in-progress", "resolved"],
            ["in-progress"] = ["blocked", "resolved"],
            ["blocked"] = ["in-progress", "resolved"],
            ["resolved"] = [],
        },
        InitialState = "needs-triage",
    };
}