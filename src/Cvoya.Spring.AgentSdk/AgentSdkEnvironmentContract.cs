// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.AgentSdk;

internal static class AgentSdkEnvironmentContract
{
    public const string CallbackUrlEnvVar = "SPRING_CALLBACK_URL";

    public const string CallbackTokenEnvVar = "SPRING_CALLBACK_TOKEN";

    public const string OrchestrationRoutePrefix = "/v1/runtime/orchestration";
}
