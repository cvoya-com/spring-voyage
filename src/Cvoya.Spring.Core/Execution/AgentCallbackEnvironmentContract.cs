// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Execution;

/// <summary>
/// Environment contract for the API host's OTLP-ingest callback surface.
/// </summary>
/// <remarks>
/// ADR-0054 collapsed the platform MCP tool surface onto a single server
/// authenticated by the MCP session token, so the messaging callback API and
/// its per-turn JWT are gone. These two env vars survive solely as the
/// OTLP-ingest credential (<c>OtlpCallbackAuthHandler</c> validates
/// <c>SPRING_CALLBACK_TOKEN</c>; <c>LauncherOtelEnvironment</c> derives the
/// <c>/otlp</c> endpoint from <c>SPRING_CALLBACK_URL</c>). Migrating OTLP off
/// this token is tracked by issue #2588; until then the contract stays.
/// </remarks>
public static class AgentCallbackEnvironmentContract
{
    /// <summary>Env var containing the API host's agent-reachable base URL.</summary>
    public const string CallbackUrlEnvVar = "SPRING_CALLBACK_URL";

    /// <summary>Env var containing the OTLP-ingest bearer token.</summary>
    public const string CallbackTokenEnvVar = "SPRING_CALLBACK_TOKEN";
}
