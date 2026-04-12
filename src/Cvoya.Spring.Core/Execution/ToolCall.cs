// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Execution;

using System.Text.Json;

/// <summary>
/// Represents a single tool invocation requested by the AI model.
/// </summary>
/// <param name="Id">The provider-assigned identifier correlating the call with its result.</param>
/// <param name="Name">The name of the tool to execute.</param>
/// <param name="Input">The JSON-encoded input arguments for the tool.</param>
public record ToolCall(string Id, string Name, JsonElement Input);