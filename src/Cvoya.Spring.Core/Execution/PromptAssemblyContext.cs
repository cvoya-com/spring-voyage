// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Execution;

using System.Text.Json;

using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Core.Skills;

/// <summary>
/// Holds all input data needed for prompt assembly across the four layers.
/// </summary>
/// <param name="Members">The addresses of peer agents in the unit.</param>
/// <param name="Policies">Optional unit policies as a JSON element.</param>
/// <param name="Skills">Optional skills available to the agent.</param>
/// <param name="PriorMessages">Prior messages in the conversation.</param>
/// <param name="LastCheckpoint">Optional last checkpoint state.</param>
/// <param name="AgentInstructions">Optional agent-specific instructions (Layer 4).</param>
public record PromptAssemblyContext(
    IReadOnlyList<Address> Members,
    JsonElement? Policies,
    IReadOnlyList<Skill>? Skills,
    IReadOnlyList<Message> PriorMessages,
    string? LastCheckpoint,
    string? AgentInstructions);