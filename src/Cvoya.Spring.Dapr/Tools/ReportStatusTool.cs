// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Tools;

using System.Text.Json;
using Cvoya.Spring.Core.Tools;
using Microsoft.Extensions.Logging;

/// <summary>
/// Platform tool that reports the agent's current status.
/// In Phase 1, this logs the status text. Full activity stream integration comes in Phase 2.
/// </summary>
public class ReportStatusTool(
    ToolExecutionContextAccessor contextAccessor,
    ILoggerFactory loggerFactory) : IPlatformTool
{
    private static readonly JsonElement Schema = JsonSerializer.SerializeToElement(new
    {
        type = "object",
        properties = new
        {
            status = new
            {
                type = "string",
                description = "The status text to report."
            }
        },
        required = new[] { "status" },
        additionalProperties = false
    });

    private readonly ILogger _logger = loggerFactory.CreateLogger<ReportStatusTool>();

    /// <inheritdoc />
    public string Name => "reportStatus";

    /// <inheritdoc />
    public string Description => "Update the activity stream with the current status.";

    /// <inheritdoc />
    public JsonElement ParametersSchema => Schema;

    /// <inheritdoc />
    public Task<JsonElement> ExecuteAsync(
        JsonElement parameters,
        JsonElement context,
        CancellationToken cancellationToken = default)
    {
        var executionContext = contextAccessor.Current
            ?? throw new InvalidOperationException("Tool execution context is not set.");

        var status = parameters.GetProperty("status").GetString()
            ?? throw new InvalidOperationException("The 'status' parameter is required.");

        _logger.LogInformation("Agent {AgentPath} reports status: {Status}",
            executionContext.AgentAddress.Path, status);

        var result = JsonSerializer.SerializeToElement(new { Success = true });
        return Task.FromResult(result);
    }
}
