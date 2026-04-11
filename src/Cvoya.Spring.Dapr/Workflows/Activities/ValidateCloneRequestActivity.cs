// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Workflows.Activities;

using Cvoya.Spring.Core.Cloning;
using Cvoya.Spring.Core.State;
using Cvoya.Spring.Dapr.Actors;

using global::Dapr.Workflow;

using Microsoft.Extensions.Logging;

/// <summary>
/// Validates a clone request by checking the cloning policy, max clone limit, and budget.
/// Returns <c>true</c> when the request is valid; <c>false</c> otherwise.
/// </summary>
public class ValidateCloneRequestActivity(
    IStateStore stateStore,
    ILoggerFactory loggerFactory) : WorkflowActivity<CloningInput, bool>
{
    private readonly ILogger _logger = loggerFactory.CreateLogger<ValidateCloneRequestActivity>();

    /// <inheritdoc />
    public override async Task<bool> RunAsync(WorkflowActivityContext context, CloningInput input)
    {
        if (string.IsNullOrWhiteSpace(input.SourceAgentId))
        {
            _logger.LogWarning("Clone request validation failed: SourceAgentId is empty");
            return false;
        }

        if (string.IsNullOrWhiteSpace(input.TargetAgentId))
        {
            _logger.LogWarning("Clone request validation failed: TargetAgentId is empty");
            return false;
        }

        if (input.CloningPolicy == CloningPolicy.None)
        {
            _logger.LogWarning("Clone request validation failed: CloningPolicy is None for agent {AgentId}",
                input.SourceAgentId);
            return false;
        }

        // Enforce max clones limit.
        if (input.MaxClones.HasValue)
        {
            var stateKey = $"{input.SourceAgentId}:{StateKeys.CloneChildren}";
            var existingClones = await stateStore.GetAsync<List<string>>(stateKey);
            var currentCount = existingClones?.Count ?? 0;

            if (currentCount >= input.MaxClones.Value)
            {
                _logger.LogWarning(
                    "Clone request validation failed: agent {AgentId} has {CurrentClones} clones, max is {MaxClones}",
                    input.SourceAgentId, currentCount, input.MaxClones.Value);
                return false;
            }
        }

        // Validate budget if specified.
        if (input.Budget.HasValue && input.Budget.Value <= 0)
        {
            _logger.LogWarning("Clone request validation failed: Budget must be greater than zero");
            return false;
        }

        _logger.LogInformation("Clone request validated for agent {AgentId} with policy {CloningPolicy}",
            input.SourceAgentId, input.CloningPolicy);
        return true;
    }
}