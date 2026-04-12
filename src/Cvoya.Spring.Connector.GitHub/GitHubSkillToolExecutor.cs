// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connector.GitHub;

using Cvoya.Spring.Core.Execution;
using Cvoya.Spring.Core.Skills;

using Microsoft.Extensions.Logging;

/// <summary>
/// Dispatches GitHub tool invocations for the multi-turn tool-use loop.
/// This implementation registers that the tool is known and returns a stub
/// response, so the loop can flow end-to-end while real GitHub operations
/// are wired up in a follow-up.
/// </summary>
/// <remarks>
/// TODO: wire real GitHub operations — follow-up after tool-use loop lands.
/// </remarks>
public class GitHubSkillToolExecutor : ISkillToolExecutor
{
    /// <summary>
    /// The prefix shared by every GitHub connector tool name.
    /// </summary>
    public const string ToolNamePrefix = "github_";

    private readonly ILogger _logger;

    /// <summary>
    /// Initializes a new instance of <see cref="GitHubSkillToolExecutor"/>.
    /// </summary>
    /// <param name="loggerFactory">Factory used to create the executor's logger.</param>
    public GitHubSkillToolExecutor(ILoggerFactory loggerFactory)
    {
        ArgumentNullException.ThrowIfNull(loggerFactory);
        _logger = loggerFactory.CreateLogger<GitHubSkillToolExecutor>();
    }

    /// <inheritdoc />
    public bool CanHandle(string toolName)
    {
        ArgumentNullException.ThrowIfNull(toolName);
        return toolName.StartsWith(ToolNamePrefix, StringComparison.Ordinal);
    }

    /// <inheritdoc />
    public Task<ToolResult> ExecuteAsync(ToolCall call, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(call);

        // TODO: wire real GitHub operations — follow-up after tool-use loop lands.
        _logger.LogInformation(
            "GitHub tool '{ToolName}' invoked (id {ToolUseId}); stub handler returning placeholder response.",
            call.Name, call.Id);

        var content = $"github tool '{call.Name}' is registered but the handler is not yet wired. Input: {call.Input.GetRawText()}";
        return Task.FromResult(new ToolResult(ToolUseId: call.Id, Content: content, IsError: false));
    }
}