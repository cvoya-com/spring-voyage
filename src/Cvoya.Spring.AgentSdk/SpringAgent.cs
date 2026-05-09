// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.AgentSdk;

using Cvoya.Spring.Core.Execution;

/// <summary>
/// Entry point for runtime images running inside Spring Voyage.
/// </summary>
public static class SpringAgent
{
    /// <summary>
    /// Creates an OrchestrationClient configured from the standard environment variables.
    /// Throws MissingCallbackEnvironmentException if SPRING_CALLBACK_URL or
    /// SPRING_CALLBACK_TOKEN are absent.
    /// </summary>
    public static IOrchestrationClient FromEnvironment()
    {
        var callbackUrl = ReadRequiredEnvironmentVariable(
            AgentCallbackEnvironmentContract.CallbackUrlEnvVar);
        var callbackToken = ReadRequiredEnvironmentVariable(
            AgentCallbackEnvironmentContract.CallbackTokenEnvVar);

        return new OrchestrationClient(callbackUrl, callbackToken);
    }

    private static string ReadRequiredEnvironmentVariable(string variableName)
    {
        var value = Environment.GetEnvironmentVariable(variableName);
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new MissingCallbackEnvironmentException(variableName);
        }

        return value;
    }
}
