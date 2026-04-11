// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Workflows.Activities;

using Cvoya.Spring.Core.Cloning;
using Cvoya.Spring.Core.Directory;
using Cvoya.Spring.Core.Messaging;

using global::Dapr.Workflow;

using Microsoft.Extensions.Logging;

/// <summary>
/// Registers a clone agent in the platform directory, with metadata reflecting
/// the <see cref="AttachmentMode"/> and parent relationship.
/// </summary>
public class RegisterCloneActivity(
    IDirectoryService directoryService,
    ILoggerFactory loggerFactory) : WorkflowActivity<CloningInput, bool>
{
    private readonly ILogger _logger = loggerFactory.CreateLogger<RegisterCloneActivity>();

    /// <inheritdoc />
    public override async Task<bool> RunAsync(WorkflowActivityContext context, CloningInput input)
    {
        var address = new Address("agent", input.TargetAgentId);

        var description = input.AttachmentMode == AttachmentMode.Attached
            ? $"Clone of {input.SourceAgentId} (attached)"
            : $"Clone of {input.SourceAgentId} (detached)";

        var entry = new DirectoryEntry(
            address,
            ActorId: input.TargetAgentId,
            DisplayName: $"Clone:{input.TargetAgentId}",
            Description: description,
            Role: null,
            RegisteredAt: DateTimeOffset.UtcNow);

        await directoryService.RegisterAsync(entry);

        _logger.LogInformation(
            "Registered clone {CloneId} in directory with attachment mode {AttachmentMode}",
            input.TargetAgentId, input.AttachmentMode);

        return true;
    }
}