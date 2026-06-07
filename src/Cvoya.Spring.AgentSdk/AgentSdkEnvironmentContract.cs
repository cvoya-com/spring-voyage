// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.AgentSdk;

/// <summary>
/// Environment-variable contract the SDK reads to reach the platform MCP
/// server (ADR-0054). The messaging tools <c>sv.messaging.send</c> /
/// <c>sv.messaging.multicast</c> are served by the single platform MCP
/// server alongside every other <c>sv.*</c> tool, so the SDK uses the same
/// MCP endpoint and session token the runtime already receives.
/// </summary>
internal static class AgentSdkEnvironmentContract
{
    /// <summary>Env var carrying the platform MCP server URL (D1 spec § 2.2.1).</summary>
    public const string McpUrlEnvVar = "SPRING_MCP_URL";

    /// <summary>Env var carrying the MCP session bearer token (D1 spec § 2.2.1).</summary>
    public const string McpTokenEnvVar = "SPRING_MCP_TOKEN";
}
