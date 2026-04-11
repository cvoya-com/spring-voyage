// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Tools;

using System.Text.Json;

/// <summary>
/// Represents a platform tool that can be invoked by an agent's AI during execution.
/// Platform tools expose platform capabilities (messaging, directory, state) as callable tools.
/// </summary>
public interface IPlatformTool
{
    /// <summary>
    /// Gets the unique name of the tool (e.g., "checkMessages", "discoverPeers").
    /// </summary>
    string Name { get; }

    /// <summary>
    /// Gets a human-readable description of what the tool does.
    /// </summary>
    string Description { get; }

    /// <summary>
    /// Gets the JSON schema defining the tool's input parameters.
    /// </summary>
    JsonElement ParametersSchema { get; }

    /// <summary>
    /// Executes the tool with the given parameters and context.
    /// </summary>
    /// <param name="parameters">The tool input parameters as JSON.</param>
    /// <param name="context">Execution context containing agent address, conversation ID, and state access.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The tool result as a JSON element.</returns>
    Task<JsonElement> ExecuteAsync(JsonElement parameters, JsonElement context, CancellationToken cancellationToken = default);
}