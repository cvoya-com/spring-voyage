// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connector.GitHub.Skills;

using System.Text.Json;

using Cvoya.Spring.Connector.GitHub.Labels;

using Microsoft.Extensions.Logging;

using Octokit;

/// <summary>
/// Transitions an issue or pull request from one state label to another by
/// consulting the configured <see cref="LabelStateMachine"/>. Illegal
/// transitions are rejected up-front with the set of valid destinations from
/// the current state so callers can recover without a round-trip.
/// </summary>
public class LabelTransitionSkill(
    IGitHubClient gitHubClient,
    LabelStateMachine stateMachine,
    ILoggerFactory loggerFactory)
{
    private readonly ILogger _logger = loggerFactory.CreateLogger<LabelTransitionSkill>();

    /// <summary>
    /// Attempts to move the given issue / PR to <paramref name="toState"/> by
    /// removing the current state label and adding the new one atomically from
    /// the caller's perspective. No-op when the issue is already in
    /// <paramref name="toState"/>.
    /// </summary>
    public async Task<JsonElement> ExecuteAsync(
        string owner,
        string repo,
        int number,
        string toState,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(toState);

        if (!stateMachine.IsStateLabel(toState))
        {
            throw new ArgumentException(
                $"'{toState}' is not a configured state label. Known states: {string.Join(", ", stateMachine.States)}.",
                nameof(toState));
        }

        _logger.LogInformation(
            "Transitioning {Owner}/{Repo}#{Number} to state label '{ToState}'",
            owner, repo, number, toState);

        var currentLabels = await gitHubClient.Issue.Labels.GetAllForIssue(owner, repo, number);
        var currentStateLabel = currentLabels
            .Select(l => l.Name)
            .FirstOrDefault(n => stateMachine.IsStateLabel(n));

        if (string.Equals(currentStateLabel, toState, StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogDebug(
                "{Owner}/{Repo}#{Number} already at state '{ToState}'; no-op",
                owner, repo, number, toState);
            return JsonSerializer.SerializeToElement(new
            {
                transitioned = false,
                from = currentStateLabel,
                to = toState,
                reason = "already_in_target_state",
            });
        }

        if (!stateMachine.IsLegalTransition(currentStateLabel, toState))
        {
            var valid = currentStateLabel is null
                ? (stateMachine.InitialState is null ? [] : new[] { stateMachine.InitialState })
                : stateMachine.ValidTransitionsFrom(currentStateLabel).ToArray();

            throw new InvalidLabelTransitionException(
                currentStateLabel,
                toState,
                valid);
        }

        if (!string.IsNullOrEmpty(currentStateLabel))
        {
            try
            {
                await gitHubClient.Issue.Labels.RemoveFromIssue(owner, repo, number, currentStateLabel);
            }
            catch (NotFoundException)
            {
                _logger.LogDebug(
                    "State label {Label} was missing when removing from {Owner}/{Repo}#{Number}; continuing",
                    currentStateLabel, owner, repo, number);
            }
        }

        var updated = await gitHubClient.Issue.Labels.AddToIssue(owner, repo, number, [toState]);

        return JsonSerializer.SerializeToElement(new
        {
            transitioned = true,
            from = currentStateLabel,
            to = toState,
            labels = updated.Select(l => l.Name).ToArray(),
        });
    }
}

/// <summary>
/// Thrown by <see cref="LabelTransitionSkill"/> when the requested transition
/// is not allowed by the configured state machine. The exception carries the
/// attempted transition and the list of legal destinations from the current
/// state so callers can surface actionable diagnostics.
/// </summary>
public sealed class InvalidLabelTransitionException : InvalidOperationException
{
    /// <summary>
    /// Initializes a new <see cref="InvalidLabelTransitionException"/>.
    /// </summary>
    public InvalidLabelTransitionException(
        string? from,
        string to,
        IReadOnlyCollection<string> validTransitionsFromCurrent)
        : base(BuildMessage(from, to, validTransitionsFromCurrent))
    {
        From = from;
        To = to;
        ValidTransitionsFromCurrent = validTransitionsFromCurrent;
    }

    /// <summary>The state the issue was in when the transition was requested.</summary>
    public string? From { get; }

    /// <summary>The state the caller attempted to move to.</summary>
    public string To { get; }

    /// <summary>Legal destinations from <see cref="From"/> under the current configuration.</summary>
    public IReadOnlyCollection<string> ValidTransitionsFromCurrent { get; }

    private static string BuildMessage(string? from, string to, IReadOnlyCollection<string> valid)
    {
        var fromDisplay = string.IsNullOrWhiteSpace(from) ? "<none>" : from;
        var validDisplay = valid.Count == 0 ? "<none>" : string.Join(", ", valid);
        return $"Illegal label transition from '{fromDisplay}' to '{to}'. Valid transitions from current state: [{validDisplay}].";
    }
}