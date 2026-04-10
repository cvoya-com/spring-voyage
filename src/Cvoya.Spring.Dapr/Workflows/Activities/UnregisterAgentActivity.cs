// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Workflows.Activities;

using Cvoya.Spring.Core.Directory;
using Cvoya.Spring.Core.Messaging;
using global::Dapr.Workflow;
using Microsoft.Extensions.Logging;

/// <summary>
/// Removes an agent from the platform directory by calling
/// <see cref="IDirectoryService.UnregisterAsync"/>.
/// </summary>
public class UnregisterAgentActivity(
    IDirectoryService directoryService,
    ILoggerFactory loggerFactory) : WorkflowActivity<AgentLifecycleInput, bool>
{
    private readonly ILogger _logger = loggerFactory.CreateLogger<UnregisterAgentActivity>();

    /// <inheritdoc />
    public override async Task<bool> RunAsync(WorkflowActivityContext context, AgentLifecycleInput input)
    {
        var address = new Address("agent", input.AgentId);

        await directoryService.UnregisterAsync(address);

        _logger.LogInformation("Unregistered agent {AgentId} from directory", input.AgentId);
        return true;
    }
}
