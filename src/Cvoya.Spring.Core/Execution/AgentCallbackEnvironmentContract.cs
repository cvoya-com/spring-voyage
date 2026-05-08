// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Execution;

/// <summary>
/// Environment contract used by runtime images that call the dispatcher
/// orchestration callback API.
/// </summary>
public static class AgentCallbackEnvironmentContract
{
    /// <summary>Env var containing the dispatcher orchestration callback base URL.</summary>
    public const string CallbackUrlEnvVar = "SPRING_CALLBACK_URL";

    /// <summary>Env var containing the per-invocation callback bearer token.</summary>
    public const string CallbackTokenEnvVar = "SPRING_CALLBACK_TOKEN";

    /// <summary>Dispatcher route prefix for the orchestration callback API.</summary>
    public const string OrchestrationRoutePrefix = "/v1/runtime/orchestration";
}