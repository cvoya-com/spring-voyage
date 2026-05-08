// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.AgentRuntimes.Launchers;

internal static class OrchestrationToolsContract
{
    /// <summary>
    /// Env var the launcher writes when the agent has children.
    /// The value is a JSON-serialised <c>OrchestrationToolDescriptor[]</c>.
    /// </summary>
    public const string EnvVar = "SPRING_ORCHESTRATION_TOOLS";
}