// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Workflows.Activities;

using Cvoya.Spring.Core.Directory;
using Cvoya.Spring.Core.Messaging;

using global::Dapr.Workflow;

using Microsoft.Extensions.Logging;

/// <summary>
/// Registers an agent in the platform directory by calling
/// <see cref="IDirectoryService.RegisterAsync"/>.
/// </summary>
public class RegisterAgentActivity(
    IDirectoryService directoryService,
    ILoggerFactory loggerFactory) : WorkflowActivity<AgentLifecycleInput, bool>
{
    private readonly ILogger _logger = loggerFactory.CreateLogger<RegisterAgentActivity>();

    /// <inheritdoc />
    public override async Task<bool> RunAsync(WorkflowActivityContext context, AgentLifecycleInput input)
    {
        var address = new Address("agent", input.AgentId);
        var entry = new DirectoryEntry(
            address,
            ActorId: input.AgentId,
            DisplayName: input.AgentName ?? input.AgentId,
            Description: $"Agent {input.AgentName ?? input.AgentId}",
            Role: input.Role,
            RegisteredAt: DateTimeOffset.UtcNow);

        await directoryService.RegisterAsync(entry);

        _logger.LogInformation("Registered agent {AgentId} in directory", input.AgentId);
        return true;
    }
}